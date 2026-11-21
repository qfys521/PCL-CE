namespace PCL.Portable;

/// <summary>用户选择的下载源。<c>Auto</c> 官方优先、失败时回退到公开镜像；<c>Mirror</c> 镜像优先。</summary>
public enum DownloadSource
{
    Auto,
    Official,
    Mirror
}

/// <summary>
/// Linux 下载策略：在镜像与官方端点之间做映射和排序。
/// 所有逻辑仅用于便携启动核心；Windows 版不经过这里，因此不会改变 Windows 行为。
/// </summary>
public static class DownloadPolicy
{
    /// <summary>BMCLAPI 公共镜像（OpenBMCLAPI 兼容端点）。</summary>
    public const string MirrorHost = "bmclapi2.bangbang93.com";

    /// <summary>环境变量，可在不修改设置文件的情况下强制下载源（auto / official / mirror）。</summary>
    public const string SourceEnvironmentVariable = "PCL_CE_DOWNLOAD_SOURCE";

    /// <summary>
    /// Mojang 下载端点 → 镜像路径前缀。空字符串表示直接保留原路径；
    /// 认证与 API 端点（api.minecraftservices.com、session.minecraft.net 等）不在表中，永不改写。
    /// </summary>
    private static readonly Dictionary<string, string> MirrorMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["piston-meta.mojang.com"] = "",
        ["launchermeta.mojang.com"] = "",
        ["launcher.mojang.com"] = "",
        ["piston-data.mojang.com"] = "",
        ["libraries.minecraft.net"] = "/maven",
        ["resources.download.minecraft.net"] = "/assets"
    };

    /// <summary>
    /// 程序化设置的下载源（优先级低于环境变量）；供 Linux UI 把用户设置接入下载层。
    /// </summary>
    public static string? SourceOverride { get; set; }

    /// <summary>当前下载源字符串：环境变量优先，其次程序化设置，最后默认 <c>auto</c>。</summary>
    public static string CurrentSource
    {
        get
        {
            var environment = Environment.GetEnvironmentVariable(SourceEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(SourceOverride) ? "auto" : SourceOverride.Trim().ToLowerInvariant();
        }
    }

    /// <summary>解析 <c>auto</c> / <c>official</c> / <c>mirror</c>；未知值回退到 <see cref="DownloadSource.Auto"/>。</summary>
    public static DownloadSource ParseSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "official" => DownloadSource.Official,
        "mirror" => DownloadSource.Mirror,
        _ => DownloadSource.Auto
    };

    /// <summary>当前生效的下载源。</summary>
    public static DownloadSource Current => ParseSource(CurrentSource);

    /// <summary>此 URL 是否为可映射到 BMCLAPI 的 Mojang 下载端点。认证与第三方端点返回 <c>false</c>。</summary>
    public static bool IsMojangEndpoint(Uri address) =>
        address is { IsAbsoluteUri: true } && MirrorMappings.ContainsKey(address.Host);

    /// <summary>
    /// 将 Mojang 官方下载地址改写为 BMCLAPI 等价地址，保留路径、查询与片段；
    /// 未知端点原样返回（认证、Modrinth、CurseForge、Adoptium 等不会被改写）。
    /// </summary>
    public static Uri RewriteToMirror(Uri official)
    {
        ArgumentNullException.ThrowIfNull(official);
        if (!IsMojangEndpoint(official)) return official;
        var prefix = MirrorMappings[official.Host];
        var path = prefix + official.PathAndQuery;
        if (!path.StartsWith('/')) path = "/" + path;
        return new Uri($"https://{MirrorHost}{path}{official.Fragment}");
    }
}
