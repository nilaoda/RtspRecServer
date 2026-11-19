using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Serilog;

namespace RtspRecServer.Server.Recording;

internal sealed class UdpDatagram : IDisposable
{
    private readonly byte[] _buffer;
    private int _references = 1;

    public UdpDatagram(ReadOnlySpan<byte> payload, long receivedTimestamp, IPEndPoint? remote = null)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(_buffer);
        Length = payload.Length;
        ReceivedTimestamp = receivedTimestamp;
        SourceAddress = remote?.Address;
        SourcePort = remote?.Port ?? 0;
    }

    public int Length { get; }
    public long ReceivedTimestamp { get; }
    public IPAddress? SourceAddress { get; }
    public int SourcePort { get; }
    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(0, Length);

    // 接收循环、订阅队列和重排窗口各持有独立的引用。
    public void Retain() => Interlocked.Increment(ref _references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}

internal readonly record struct UdpReceiverKey(IPAddress BindAddress, IPAddress? MulticastInterface, int Port)
{
    public static UdpReceiverKey ForSubscription(IPAddress? group, IPAddress? localAddress, int port) => group is null
        ? new UdpReceiverKey(localAddress ?? IPAddress.Any, null, port)
        : new UdpReceiverKey(IPAddress.Any, localAddress, port);
}

internal sealed class UdpSubscription : IAsyncDisposable
{
    private readonly Channel<UdpDatagram> _channel;
    private readonly Action<UdpSubscription> _onDispose;
    private int _disposed;

    internal UdpSubscription(
        IPAddress? groupAddress, int capacity, Action<UdpSubscription> onDispose, Action<UdpSubscription> onDrop)
    {
        GroupAddress = groupAddress;
        _onDispose = onDispose;
        _channel = Channel.CreateBounded<UdpDatagram>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        }, dropped =>
        {
            dropped.Dispose();
            onDrop(this);
        });
    }

    public IPAddress? GroupAddress { get; }
    public ChannelReader<UdpDatagram> Reader => _channel.Reader;
    internal long DroppedCount;

    internal bool TryWrite(UdpDatagram datagram) => _channel.Writer.TryWrite(datagram);

    internal void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onDispose(this);
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var datagram))
            {
                datagram.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class UdpEndpointReceiver : IDisposable
{
    private const int ReceiveBufferBytes = 16 * 1024 * 1024;
    private const int LinuxIpMulticastAll = 49;
    private const int BsdSoReusePort = 0x0200;
    private static readonly TimeSpan MembershipRefreshInterval = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly Socket _socket;
    private readonly IPAddress? _multicastInterface;
    private readonly int _port;
    private readonly Func<int, UdpSubscription[]> _getWildcardSubscribers;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<UdpSubscription> _subscriptions = [];
    private readonly Dictionary<IPAddress, int> _groupRefs = [];
    private readonly Timer _membershipRefresh;
    private UdpSubscription[] _snapshot = [];
    private bool _disposed;

    public UdpEndpointReceiver(UdpReceiverKey key, Func<int, UdpSubscription[]> getWildcardSubscribers)
    {
        _multicastInterface = key.MulticastInterface;
        _port = key.Port;
        _getWildcardSubscribers = getWildcardSubscribers;
        _socket = CreateSocket(key.BindAddress, key.Port);
        _membershipRefresh = new Timer(RefreshMemberships, null, MembershipRefreshInterval, MembershipRefreshInterval);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public UdpSubscription[] GetSubscribers() => Volatile.Read(ref _snapshot);

    public UdpSubscription Subscribe(IPAddress? groupAddress, int capacity, Action<UdpSubscription> onDispose)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UdpEndpointReceiver));
            }

            if (groupAddress is not null)
            {
                if (!_groupRefs.TryGetValue(groupAddress, out var count))
                {
                    JoinGroup(groupAddress);
                }

                _groupRefs[groupAddress] = count + 1;
            }

            var subscription = new UdpSubscription(groupAddress, capacity, onDispose, ReportDrop);
            _subscriptions.Add(subscription);
            Volatile.Write(ref _snapshot, _subscriptions.ToArray());
            return subscription;
        }
    }

    internal bool Remove(UdpSubscription subscription)
    {
        bool empty;
        lock (_sync)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return false;
            }

            if (subscription.GroupAddress is not null &&
                _groupRefs.TryGetValue(subscription.GroupAddress, out var count))
            {
                if (count <= 1)
                {
                    _groupRefs.Remove(subscription.GroupAddress);
                    DropGroup(subscription.GroupAddress);
                }
                else
                {
                    _groupRefs[subscription.GroupAddress] = count - 1;
                }
            }

            empty = _subscriptions.Count == 0;
            Volatile.Write(ref _snapshot, _subscriptions.ToArray());
        }

        return empty;
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _subscriptions.Count == 0;
            }
        }
    }

    public void Dispose()
    {
        UdpSubscription[] subscriptions;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
            Volatile.Write(ref _snapshot, []);
        }

        _membershipRefresh.Dispose();
        _cancellation.Cancel();
        _socket.Close();
        foreach (var subscription in subscriptions)
        {
            subscription.Complete();
        }

        _cancellation.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        var consecutiveErrors = 0;
        var reportedMissingDestination = false;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult received;
                try
                {
                    received = await _socket.ReceiveMessageFromAsync(
                        buffer.AsMemory(0, buffer.Length),
                        SocketFlags.None,
                        remoteEndPoint,
                        _cancellation.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!_cancellation.IsCancellationRequested)
                {
                    // Windows 上 ICMP 不可达等瞬时错误不应终结整个端口的所有录制
                    consecutiveErrors++;
                    if (consecutiveErrors == 1 || consecutiveErrors % 100 == 0)
                    {
                        Log.Warning(ex, "UDP 接收暂时失败，继续重试（第 {Count} 次）：端口 {Port}", consecutiveErrors, _port);
                    }

                    await Task.Delay(Math.Min(100 * consecutiveErrors, 1000), _cancellation.Token).ConfigureAwait(false);
                    continue;
                }

                consecutiveErrors = 0;
                if (received.ReceivedBytes <= 0 || (received.SocketFlags & SocketFlags.Truncated) != 0)
                {
                    continue;
                }

                var destination = received.PacketInformation.Address;
                var subscribers = GetSubscribers();
                if ((destination is null || destination.Equals(IPAddress.Any)) &&
                    Array.Exists(subscribers, static target => target.GroupAddress is not null))
                {
                    if (!reportedMissingDestination)
                    {
                        reportedMissingDestination = true;
                        Log.Warning("UDP 数据报缺少目标地址，无法按组播组分发，端口 {Port}", _port);
                    }
                    continue;
                }

                if (_multicastInterface is not null && destination is not null && !IsMulticast(destination))
                {
                    // 通配地址同端口的 socket 可能被内核选中接收单播，交回该端口的单播订阅。
                    subscribers = _getWildcardSubscribers(_port);
                }

                var source = received.RemoteEndPoint as IPEndPoint;
                var receivedTimestamp = Stopwatch.GetTimestamp();
                UdpDatagram? datagram = null;
                try
                {
                    foreach (var target in subscribers)
                    {
                        if (!ShouldDeliver(target.GroupAddress, destination))
                        {
                            continue;
                        }

                        datagram ??= new UdpDatagram(buffer.AsSpan(0, received.ReceivedBytes), receivedTimestamp, source);
                        datagram.Retain();
                        if (!target.TryWrite(datagram))
                        {
                            datagram.Dispose();
                        }
                    }
                }
                finally
                {
                    datagram?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UDP 接收循环异常，端口: {Port}", _port);
            UdpSubscription[] subscriptions;
            lock (_sync)
            {
                subscriptions = _subscriptions.ToArray();
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Complete(ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReportDrop(UdpSubscription target)
    {
        var dropped = Interlocked.Increment(ref target.DroppedCount);
        if (dropped == 1 || dropped % 1000 == 0)
        {
            Log.Warning(
                "UDP 接收队列积压，已丢弃 {Count} 个数据报：端口 {Port}，组播组 {Group}",
                dropped,
                _port,
                target.GroupAddress?.ToString() ?? "单播");
        }
    }

    private static Socket CreateSocket(IPAddress bindAddress, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (bindAddress.Equals(IPAddress.Any))
            {
                try
                {
                    if (OperatingSystem.IsLinux())
                    {
                        // 只收当前 socket 加入的组播，避免其他组的流量填满接收队列。
                        socket.SetRawSocketOption((int)SocketOptionLevel.IP, LinuxIpMulticastAll, BitConverter.GetBytes(0));
                    }
                    else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                    {
                        // macOS/BSD 的通配地址同端口绑定还需要 SO_REUSEPORT。
                        socket.SetRawSocketOption((int)SocketOptionLevel.Socket, BsdSoReusePort, BitConverter.GetBytes(1));
                    }
                }
                catch (SocketException ex)
                {
                    Log.Debug(ex, "设置 UDP 通配地址选项失败，端口 {Port}", port);
                }
            }
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("无法启用 UDP 目标地址信息，不能按组播组分发数据", ex);
            }

            try
            {
                socket.ReceiveBufferSize = ReceiveBufferBytes;
                var reportedBuffer = socket.ReceiveBufferSize;
                // Linux SO_RCVBUF 汇报的值包含内核记账空间，是实际设置值的两倍。
                var appliedBuffer = OperatingSystem.IsLinux() ? reportedBuffer / 2 : reportedBuffer;
                if (appliedBuffer < ReceiveBufferBytes)
                {
                    Log.Warning("UDP Socket 接收缓冲小于请求值: 端口 {Port}，请求 {Requested} 字节，SO_RCVBUF 返回 {Actual} 字节",
                        port, ReceiveBufferBytes, reportedBuffer);
                }
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "设置共享 UDP 接收缓冲区失败，将使用系统默认值");
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // SIO_UDP_CONNRESET：禁用 ICMP 不可达触发的 ConnectionReset，避免接收循环被瞬时网络错误打断
                    socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                }
                catch (SocketException)
                {
                }
            }

            socket.Bind(new IPEndPoint(bindAddress, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void JoinGroup(IPAddress group)
    {
        var option = _multicastInterface is null
            ? new MulticastOption(group)
            : new MulticastOption(group, _multicastInterface);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
        Log.Information("UDP 共享接收加入组播组 {Group}:{Port}", group, _port);
    }

    private void RefreshMemberships(object? state)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var group in _groupRefs.Keys)
            {
                try
                {
                    var option = _multicastInterface is null
                        ? new MulticastOption(group)
                        : new MulticastOption(group, _multicastInterface);
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "刷新组播成员失败: {Group}:{Port}", group, _port);
                }
            }
        }
    }

    private void DropGroup(IPAddress group)
    {
        try
        {
            var option = _multicastInterface is null
                ? new MulticastOption(group)
                : new MulticastOption(group, _multicastInterface);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出 UDP 组播组失败: {Group}:{Port}", group, _port);
        }
    }

    internal static bool ShouldDeliver(IPAddress? subscriptionGroup, IPAddress? destination)
    {
        if (subscriptionGroup is not null)
        {
            return subscriptionGroup.Equals(destination);
        }

        return destination is null || !IsMulticast(destination);
    }

    internal static bool IsMulticast(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out var count) && count == 4 && bytes[0] is >= 224 and <= 239;
    }
}

internal sealed class UdpReceiveHub
{
    private readonly object _sync = new();
    private readonly Dictionary<UdpReceiverKey, UdpEndpointReceiver> _receivers = [];

    public UdpSubscription Subscribe(
        IPAddress? groupAddress,
        IPAddress? localAddress,
        int port,
        int capacity = 8192)
    {
        var key = UdpReceiverKey.ForSubscription(groupAddress, localAddress, port);
        lock (_sync)
        {
            if (!_receivers.TryGetValue(key, out var receiver))
            {
                receiver = new UdpEndpointReceiver(key, GetWildcardSubscribers);
                _receivers[key] = receiver;
            }
            try
            {
                return receiver.Subscribe(
                    groupAddress,
                    capacity,
                    subscription => RemoveSubscription(key, receiver, subscription));
            }
            catch
            {
                if (receiver.IsEmpty)
                {
                    _receivers.Remove(key);
                    receiver.Dispose();
                }

                throw;
            }
        }
    }

    private UdpSubscription[] GetWildcardSubscribers(int port)
    {
        lock (_sync)
        {
            return _receivers.TryGetValue(new UdpReceiverKey(IPAddress.Any, null, port), out var receiver)
                ? receiver.GetSubscribers()
                : [];
        }
    }

    private void RemoveSubscription(
        UdpReceiverKey key,
        UdpEndpointReceiver receiver,
        UdpSubscription subscription)
    {
        lock (_sync)
        {
            // 与订阅串行处理，避免关闭已经被新任务复用的 socket。
            if (receiver.Remove(subscription))
            {
                _receivers.Remove(key);
                receiver.Dispose();
            }
        }
    }
}
