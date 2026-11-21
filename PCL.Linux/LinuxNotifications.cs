using PCL.Portable;
using Tmds.DBus.Protocol;

namespace PCL.Linux;

/// <summary>
/// 通过 <c>org.freedesktop.Notifications</c> D-Bus 服务发送系统通知。
/// 绝不创建自绘弹窗：没有通知服务时安全地返回 false，并只在日志中记录一次。
/// </summary>
public static class LinuxNotifications
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";
    private const string AppName = "PCL-CE-Linux";

    private const long AvailabilityTtlMs = 30_000;
    private const long DedupWindowMs = 2_000;

    private static readonly object Gate = new();
    private static bool? _available;
    private static long _availabilityCheckedAt;
    private static string? _lastSummary;
    private static string? _lastBody;
    private static long _lastSentAt;
    private static int _noServiceLogged;

    /// <summary>当前会话 D-Bus 上是否存在通知服务（带 30 秒缓存；首次探测可能阻塞极短时间）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (CachedAvailability() is { } cached) return cached;
            var available = ProbeOwner();
            lock (Gate)
            {
                _available = available;
                _availabilityCheckedAt = Environment.TickCount64;
            }
            return available;
        }
    }

    /// <summary>
    /// 发送一条系统通知。成功返回 true；没有 D-Bus 通知服务或发送失败返回 false（不抛出）。
    /// 2 秒内连续相同的通知只发送一次。绝不同步阻塞 UI：探测与调用都在后台完成。
    /// </summary>
    public static async Task<bool> NotifyAsync(string summary, string body, string? icon = "pcl-ce", int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(summary)) return false;
        body ??= "";
        lock (Gate)
        {
            if (string.Equals(_lastSummary, summary, StringComparison.Ordinal) &&
                string.Equals(_lastBody, body, StringComparison.Ordinal) &&
                Environment.TickCount64 - _lastSentAt < DedupWindowMs)
                return true;
        }

        var available = CachedAvailability() ?? await ProbeOwnerAsync().ConfigureAwait(false);
        lock (Gate)
        {
            _available = available;
            _availabilityCheckedAt = Environment.TickCount64;
        }
        if (!available)
        {
            LogUnavailableOnce();
            AppLog.Info($"系统通知不可用，内容：{summary} / {body}");
            return false;
        }

        try
        {
            _ = await SendNotifyAsync(summary, body, icon, timeoutMs).ConfigureAwait(false);
            lock (Gate)
            {
                _lastSummary = summary;
                _lastBody = body;
                _lastSentAt = Environment.TickCount64;
                _available = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("发送系统通知失败：" + ex.Message);
            lock (Gate) { _available = null; }
            return false;
        }
    }

    /// <summary>错误通知的便捷入口：使用系统错误图标与较长的显示时间。</summary>
    public static Task<bool> NotifyErrorAsync(string summary, string body, int timeoutMs = 8000) =>
        NotifyAsync(summary, body, "dialog-error", timeoutMs);

    private static bool? CachedAvailability()
    {
        lock (Gate)
        {
            if (_available is bool cached && Environment.TickCount64 - _availabilityCheckedAt < AvailabilityTtlMs)
                return cached;
            return null;
        }
    }

    private static bool ProbeOwner()
    {
        try
        {
            var probe = ProbeOwnerAsync();
            return probe.Wait(TimeSpan.FromSeconds(2)) && probe.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> ProbeOwnerAsync()
    {
        try
        {
            var connection = Connection.Session;
            var message = CreateNameHasOwnerMessage(connection, Service);
            return await connection.CallMethodAsync(message,
                    static (Message reply, object? _) => reply.GetBodyReader().ReadBool())
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<uint> SendNotifyAsync(string summary, string body, string? icon, int timeoutMs)
    {
        var connection = Connection.Session;
        var message = CreateNotifyMessage(connection, summary, body, icon, timeoutMs);
        return await connection.CallMethodAsync(message,
                static (Message reply, object? _) => reply.GetBodyReader().ReadUInt32())
            .WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
    }

    private static MessageBuffer CreateNameHasOwnerMessage(Connection connection, string name)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus", "NameHasOwner", "s");
            writer.WriteString(name);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer CreateNotifyMessage(Connection connection, string summary, string body, string? icon, int timeoutMs)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            // 签名：s app_name, u replaces_id, s app_icon, s summary, s body, as actions, a{sv} hints, i expire_timeout
            writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "Notify", "susssasa{sv}i");
            writer.WriteString(AppName);
            writer.WriteUInt32(0u);
            writer.WriteString(icon ?? "");
            writer.WriteString(summary);
            writer.WriteString(body);
            writer.WriteArray(Array.Empty<string>());
            var hints = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(hints);
            writer.WriteInt32(Math.Clamp(timeoutMs, -1, 60_000));
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void LogUnavailableOnce()
    {
        if (Interlocked.Exchange(ref _noServiceLogged, 1) != 0) return;
        AppLog.Warn("系统通知不可用：未找到 org.freedesktop.Notifications 服务；通知内容只会写入运行日志。");
    }
}
