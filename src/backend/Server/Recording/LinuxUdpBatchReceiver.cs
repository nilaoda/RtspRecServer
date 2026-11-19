using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Serilog;

namespace RtspRecServer.Server.Recording;

internal sealed unsafe partial class LinuxUdpBatchReceiver(SafeSocketHandle socket, int port) : IDisposable
{
    internal const int BatchSize = 32;
    private const int DatagramSize = 65536;
    private const int MessageDontWait = 0x40;
    private const int MessageTruncated = 0x20;
    private const int Interrupted = 4;
    private const int WouldBlock = 11;

    private readonly Message[] _messages = new Message[BatchSize];
    private readonly Ipv4SocketAddress[] _senders = new Ipv4SocketAddress[BatchSize];
    private byte[]? _payloads;

    internal static bool IsSupported => OperatingSystem.IsLinux() && BitConverter.IsLittleEndian &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    // 仅由接收循环调用。非阻塞读取，socket 的异步等待和取消仍由托管层负责。
    public int Read()
    {
        _payloads ??= ArrayPool<byte>.Shared.Rent(BatchSize * DatagramSize);
        var addedReference = false;
        try
        {
            socket.DangerousAddRef(ref addedReference);
            fixed (byte* payloads = _payloads)
            fixed (Message* messages = _messages)
            fixed (Ipv4SocketAddress* senders = _senders)
            {
                var buffers = stackalloc IoVector[BatchSize];
                for (var i = 0; i < BatchSize; i++)
                {
                    buffers[i] = new IoVector { Base = payloads + i * DatagramSize, Length = DatagramSize };
                    messages[i] = new Message
                    {
                        Header = new MessageHeader
                        {
                            Name = senders + i,
                            NameLength = (uint)sizeof(Ipv4SocketAddress),
                            Vector = buffers + i,
                            VectorLength = 1
                        }
                    };
                }

                var count = ReceiveMessages(socket.DangerousGetHandle().ToInt32(), messages, BatchSize, MessageDontWait, null);
                if (count >= 0)
                {
                    return count;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error is Interrupted or WouldBlock)
                {
                    return 0;
                }

                Log.Warning("Linux UDP 批量接收失败，切换为逐包接收: 端口 {Port}，errno {Error}", port, error);
                return -1;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warning("Linux 未提供 recvmmsg，切换为逐包接收: 端口 {Port}", port);
            return -1;
        }
        finally
        {
            if (addedReference)
            {
                socket.DangerousRelease();
            }
        }
    }

    public bool TryGetPacket(int index, out ReadOnlySpan<byte> payload, out IPAddress source, out int sourcePort)
    {
        ref var message = ref _messages[index];
        ref var sender = ref _senders[index];
        if (message.Length is 0 or > DatagramSize || (message.Header.Flags & MessageTruncated) != 0 ||
            message.Header.NameLength < sizeof(Ipv4SocketAddress) || sender.Family != (ushort)AddressFamily.InterNetwork)
        {
            payload = default;
            source = IPAddress.None;
            sourcePort = 0;
            return false;
        }

        payload = _payloads.AsSpan(index * DatagramSize, (int)message.Length);
        source = new IPAddress(sender.Address);
        sourcePort = BinaryPrimitives.ReverseEndianness(sender.Port);
        return true;
    }

    public void Dispose()
    {
        if (_payloads is { } payloads)
        {
            _payloads = null;
            ArrayPool<byte>.Shared.Return(payloads);
        }
    }

    [LibraryImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    private static partial int ReceiveMessages(int socket, Message* messages, uint count, int flags, void* timeout);

    // 对应 Linux x64/arm64 小端 ABI 下的 iovec、sockaddr_in、msghdr 和 mmsghdr。
    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public byte* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ipv4SocketAddress
    {
        public ushort Family;
        public ushort Port;
        public uint Address;
        private ulong _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageHeader
    {
        public Ipv4SocketAddress* Name;
        public uint NameLength;
        public IoVector* Vector;
        public nuint VectorLength;
        public void* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public MessageHeader Header;
        public uint Length;
    }
}
