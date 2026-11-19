using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Serilog;

namespace RtspRecServer.Server.Recording;

internal sealed class UdpDatagram : IDisposable
{
    public required byte[] Buffer { get; init; }
    public required int Length { get; init; }
    public required long ReceivedTimestamp { get; init; }
    public IPAddress? SourceAddress { get; init; }
    public int SourcePort { get; init; }
    public ReadOnlyMemory<byte> Payload => Buffer.AsMemory(0, Length);

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Buffer);
    }
}

internal readonly record struct UdpReceiverKey(IPAddress LocalAddress, int Port);

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
            SingleWriter = true,
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
    private static readonly TimeSpan MembershipRefreshInterval = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly Socket _socket;
    private readonly IPAddress? _localAddress;
    private readonly int _port;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<UdpSubscription> _subscriptions = [];
    private readonly Dictionary<IPAddress, int> _groupRefs = [];
    private readonly Timer _membershipRefresh;
    private UdpSubscription[] _snapshot = [];
    private bool _disposed;

    public UdpEndpointReceiver(IPAddress? localAddress, int port)
    {
        _localAddress = localAddress;
        _port = port;
        _socket = CreateSocket(localAddress, port);
        _membershipRefresh = new Timer(RefreshMemberships, null, MembershipRefreshInterval, MembershipRefreshInterval);
        _ = Task.Run(ReceiveLoopAsync);
    }

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
            _snapshot = _subscriptions.ToArray();
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
            _snapshot = _subscriptions.ToArray();
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
            _snapshot = [];
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
                if (received.ReceivedBytes <= 0)
                {
                    continue;
                }

                var destination = received.PacketInformation.Address;
                var source = received.RemoteEndPoint as IPEndPoint;
                var receivedTimestamp = Stopwatch.GetTimestamp();
                foreach (var target in Volatile.Read(ref _snapshot))
                {
                    if (!ShouldDeliver(target.GroupAddress, destination))
                    {
                        continue;
                    }

                    var copy = ArrayPool<byte>.Shared.Rent(received.ReceivedBytes);
                    buffer.AsSpan(0, received.ReceivedBytes).CopyTo(copy);
                    var datagram = new UdpDatagram
                    {
                        Buffer = copy,
                        Length = received.ReceivedBytes,
                        ReceivedTimestamp = receivedTimestamp,
                        SourceAddress = source?.Address,
                        SourcePort = source?.Port ?? 0
                    };

                    if (!target.TryWrite(datagram))
                    {
                        datagram.Dispose();
                    }
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

    private static Socket CreateSocket(IPAddress? localAddress, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
                if (OperatingSystem.IsLinux() && reportedBuffer / 2 < ReceiveBufferBytes)
                {
                    Log.Warning(
                        "UDP Socket 接收缓冲受系统限制: 端口 {Port}，请求 {Requested} 字节，SO_RCVBUF 返回 {Actual} 字节，请检查 net.core.rmem_max",
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

            socket.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, port));
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
        var option = _localAddress is null
            ? new MulticastOption(group)
            : new MulticastOption(group, _localAddress);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
        Log.Information("UDP 共享接收加入组播组 {Group}:{Port}", group, _port);
    }

    private void RefreshMemberships(object? state)
    {
        IPAddress[] groups;
        lock (_sync)
        {
            if (_disposed || _groupRefs.Count == 0)
            {
                return;
            }

            groups = _groupRefs.Keys.ToArray();
        }

        foreach (var group in groups)
        {
            try
            {
                var option = _localAddress is null
                    ? new MulticastOption(group)
                    : new MulticastOption(group, _localAddress);
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "刷新组播成员失败: {Group}:{Port}", group, _port);
            }
        }
    }

    private void DropGroup(IPAddress group)
    {
        try
        {
            var option = _localAddress is null
                ? new MulticastOption(group)
                : new MulticastOption(group, _localAddress);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出 UDP 组播组失败: {Group}:{Port}", group, _port);
        }
    }

    internal static bool ShouldDeliver(IPAddress? subscriptionGroup, IPAddress destination)
    {
        if (subscriptionGroup is not null)
        {
            return destination.Equals(IPAddress.Any) || destination.Equals(subscriptionGroup);
        }

        return !IsMulticast(destination) || destination.Equals(IPAddress.Any);
    }

    internal static bool IsMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
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
        var key = new UdpReceiverKey(localAddress ?? IPAddress.Any, port);
        lock (_sync)
        {
            if (!_receivers.TryGetValue(key, out var receiver))
            {
                receiver = new UdpEndpointReceiver(localAddress, port);
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
