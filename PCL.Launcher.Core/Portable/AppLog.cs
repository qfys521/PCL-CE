using System.Text;

namespace PCL.Portable;

/// <summary>
/// 写入 <c>$XDG_STATE_HOME/pcl-ce/logs/pcl-ce.log</c> 的启动器日志。
/// 不依赖 UI；无控制台（AppImage 双击启动）时也不会丢失错误。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _path;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string? LogPath { get { lock (Gate) return _path; } }

    /// <summary>初始化日志文件（会创建状态目录并轮转过大的旧日志）。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            try
            {
                LinuxPaths.EnsureDirectory(LinuxPaths.LogsHome,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _path = LinuxPaths.AppLogPath;
                try
                {
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".1", true);
                }
                catch (Exception) { }
                File.AppendAllText(_path, $"\n===== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} PCL-CE 启动 =====\n");
                _initialized = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"无法写入日志文件：{ex.Message}");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}\n{exception}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        lock (Gate)
        {
            if (_path is not null)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
                catch (Exception) { _initialized = false; }
            }
        }
        if (level is "WARN" or "ERROR") Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }
}
