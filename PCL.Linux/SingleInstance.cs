using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PCL.Portable;

namespace PCL.Linux;

/// <summary>
/// 单实例控制：锁文件放在 <c>$XDG_RUNTIME_DIR/pcl-ce/instance.lock</c>，
/// 第二个实例通过 <c>$XDG_RUNTIME_DIR/pcl-ce/instance.sock</c> 把命令行参数转发给已运行的实例后退出。
/// 这样双击 .mrpack 或点击 pcl-ce:// 链接时不会启动第二个启动器。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptTask;

    /// <summary>已运行实例收到的外部参数（在后台线程触发，调用方需切回 UI 线程）。</summary>
    public event Action<string[]>? ArgumentsReceived;

    private SingleInstance(FileStream lockStream, Socket listener)
    {
        _lock = lockStream;
        _listener = listener;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>尝试获取单实例锁；false 表示已有实例在运行且参数已转发。</summary>
    public static bool TryAcquire(string[] args, out SingleInstance? instance)
    {
        instance = null;
        LinuxPaths.EnsureDirectory(LinuxPaths.RuntimeHome,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(LinuxPaths.LockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }
        catch (IOException)
        {
            Forward(args);
            return false;
        }
        // 已持有锁：清理上次异常退出遗留的 socket 再监听。
        try { if (File.Exists(LinuxPaths.SocketPath)) File.Delete(LinuxPaths.SocketPath); }
        catch (Exception) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(LinuxPaths.SocketPath));
            listener.Listen(4);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"单实例 socket 无法监听，将退化为“仅锁”模式：{ex.Message}");
            listener.Dispose();
            instance = new SingleInstance(lockStream, new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified));
            return true;
        }
        instance = new SingleInstance(lockStream, listener);
        return true;
    }

    /// <summary>把参数转发给已运行实例；失败时安静退出（用户仍可看到原实例）。</summary>
    private static void Forward(string[] args)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(LinuxPaths.SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(payload.Length + "\n"));
            socket.Send(payload);
            socket.Shutdown(SocketShutdown.Send);
            var buffer = new byte[8];
            socket.ReceiveTimeout = 2000;
            _ = socket.Receive(buffer);
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
            try { client = await _listener.AcceptAsync(token); }
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
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var lengthLine = await reader.ReadLineAsync(token);
                    if (int.TryParse(lengthLine, out var length) && length is > 0 and < 1 << 20)
                    {
                        var buffer = new char[length];
                        var read = 0;
                        while (read < length)
                        {
                            var count = await reader.ReadAsync(buffer.AsMemory(read, length - read), token);
                            if (count == 0) break;
                            read += count;
                        }
                        var json = new string(buffer, 0, read);
                        var forwarded = JsonSerializer.Deserialize<string[]>(json);
                        if (forwarded is { Length: > 0 }) ArgumentsReceived?.Invoke(forwarded);
                    }
                    await stream.WriteAsync(new byte[] { 1 }, token);
                }
                catch (Exception ex) { AppLog.Warn("处理转发参数失败：" + ex.Message); }
            }, token);
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _listener.Close(0); } catch (Exception) { }
        _listener.Dispose();
        try { _cancellation.Dispose(); } catch (Exception) { }
        try { _lock.Dispose(); } catch (Exception) { }
        try { if (File.Exists(LinuxPaths.SocketPath)) File.Delete(LinuxPaths.SocketPath); } catch (Exception) { }
    }
}
