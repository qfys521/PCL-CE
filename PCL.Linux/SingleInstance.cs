using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 单实例控制：锁文件放在 <c>$XDG_RUNTIME_DIR/pcl-ce/instance.lock</c>，
/// 第二个实例通过 <c>$XDG_RUNTIME_DIR/pcl-ce/instance.sock</c> 把命令行参数转发给已运行的实例后退出。
/// 这样双击 .mrpack 或点击 pcl-ce:// 链接时不会启动第二个启动器。
/// 协议：4 字节大端长度 + UTF-8 JSON 数组 + 1 字节确认。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 1 << 20;
    private readonly FileStream _lock;
    private readonly Socket? _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptTask;

    /// <summary>已运行实例收到的外部参数（在后台线程触发，调用方需切回 UI 线程）。</summary>
    public event Action<string[]>? ArgumentsReceived;

    private SingleInstance(FileStream lockStream, Socket? listener)
    {
        _lock = lockStream;
        _listener = listener;
        _acceptTask = listener is null ? Task.CompletedTask : Task.Run(AcceptLoopAsync);
    }

    /// <summary>尝试获取单实例锁；false 表示已有实例在运行且参数已转发。</summary>
    public static bool TryAcquire(string[] args, out SingleInstance? instance)
    {
        instance = null;
        try
        {
            LinuxPaths.EnsureDirectory(LinuxPaths.RuntimeHome,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var lockStream = new FileStream(LinuxPaths.LockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            // 已持有锁：清理上次异常退出遗留的 socket 再监听。
            try { if (File.Exists(LinuxPaths.SocketPath)) File.Delete(LinuxPaths.SocketPath); }
            catch (Exception) { }
            Socket listener;
            try
            {
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(LinuxPaths.SocketPath));
                listener.Listen(4);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(LinuxPaths.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"单实例 socket 无法监听，将退化为“仅锁”模式：{ex.Message}");
                instance = new SingleInstance(lockStream, null);
                return true;
            }
            instance = new SingleInstance(lockStream, listener);
            return true;
        }
        catch (IOException)
        {
            // 锁被占用 = 已有实例在运行。
            Forward(args);
            return false;
        }
    }

    /// <summary>把参数转发给已运行实例；失败时安静退出（用户仍可看到原实例）。</summary>
    private static void Forward(string[] args)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(args);
            if (payload.Length > MaxMessageBytes) throw new InvalidDataException("转发参数过大");
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(LinuxPaths.SocketPath));
            socket.SendTimeout = 2000;
            socket.Send(header);
            var sent = 0;
            while (sent < payload.Length)
                sent += socket.Send(payload, sent, payload.Length - sent, SocketFlags.None);
            socket.Shutdown(SocketShutdown.Send);
            socket.ReceiveTimeout = 2000;
            Span<byte> ack = stackalloc byte[1];
            try { _ = socket.Receive(ack); } catch (Exception) { }
            AppLog.Info("检测到已运行的 PCL-CE，参数已转发。");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"已有实例在运行，但参数转发失败：{ex.Message}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                AppLog.Warn("单实例监听失败：" + ex.Message);
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var stream = new NetworkStream(client, true);
                    var header = new byte[4];
                    if (!await ReadExactlyAsync(stream, header, token)) return;
                    var length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length is <= 0 or > MaxMessageBytes) return;
                    var payload = new byte[length];
                    if (!await ReadExactlyAsync(stream, payload, token)) return;
                    var forwarded = JsonSerializer.Deserialize<string[]>(payload);
                    if (forwarded is { Length: > 0 }) ArgumentsReceived?.Invoke(forwarded);
                    await stream.WriteAsync(new byte[] { 1 }, token);
                }
                catch (Exception ex) { AppLog.Warn("处理转发参数失败：" + ex.Message); }
            }, token);
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), token);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _listener?.Close(0); } catch (Exception) { }
        _listener?.Dispose();
        try { _cancellation.Dispose(); } catch (Exception) { }
        try { _lock.Dispose(); } catch (Exception) { }
        try { if (File.Exists(LinuxPaths.SocketPath)) File.Delete(LinuxPaths.SocketPath); } catch (Exception) { }
    }
}
