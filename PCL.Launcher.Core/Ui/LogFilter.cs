namespace PCL;

/// <summary>日志中敏感信息的打码。</summary>
public interface ILogFilter
{
    string FilterUserName(string raw, char filterChar);
    string FilterAccessToken(string raw, char filterChar);
}

public static class LogFilter
{
    public static ILogFilter? Current { get; set; }

    public static string FilterUserName(string raw, char filterChar)
    {
        return Current?.FilterUserName(raw, filterChar) ?? raw;
    }

    public static string FilterAccessToken(string raw, char filterChar)
    {
        return Current?.FilterAccessToken(raw, filterChar) ?? raw;
    }
}

/// <summary>区域限制功能判定。</summary>
public interface IRestrictedFeatures
{
    bool IsRestricted { get; }
    bool Check(string feature);
    void ThrowIfRestricted(string feature);
}

public static class RestrictedFeatures
{
    private static IRestrictedFeatures? _current;

    /// <summary>
    /// 区域限制策略。Linux 便携核心没有任何 UI 会注册限制策略，默认也必须放行全部功能；
    /// 仅在 Windows 桌面宿主显式注入策略时才启用限制。
    /// </summary>
    public static IRestrictedFeatures? Current
    {
        get => _current ?? (OperatingSystem.IsWindows() ? null : AllAllowedRestrictedFeatures.Instance);
        set => _current = value;
    }

    public static bool IsRestricted => Current?.IsRestricted ?? false;

    public static bool Check(string feature)
    {
        return Current?.Check(feature) ?? true;
    }

    public static void ThrowIfRestricted(string feature)
    {
        Current?.ThrowIfRestricted(feature);
    }

    /// <summary>非 Windows 平台（PCL-CE-Linux）永远允许全部功能，区域限制不得阻断下载或启动。</summary>
    private sealed class AllAllowedRestrictedFeatures : IRestrictedFeatures
    {
        public static readonly AllAllowedRestrictedFeatures Instance = new();

        public bool IsRestricted => false;

        public bool Check(string feature) => true;

        public void ThrowIfRestricted(string feature)
        {
        }
    }
}