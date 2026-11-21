using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace PCL.Portable;

public sealed record LibraryDownload(string Path, string? Url, string? Hash, bool Native);

/// <summary>
/// Native Linux launch adapter with inherited profiles and isolated instances.
/// 游戏数据（versions / assets / libraries / instances）位于用户选择的游戏目录；
/// 原生库解压目录与运行日志属于启动器缓存，默认放在 XDG 缓存/状态目录（见 <see cref="LinuxPaths"/>）。
/// </summary>
public sealed class LinuxGameLauncher
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>保留的游戏运行日志数量上限。</summary>
    private const int MaxLogFiles = 30;

    /// <summary>游戏运行日志的保留天数。</summary>
    private const int MaxLogAgeDays = 14;

    public LinuxGameLauncher(string gameDirectory, HttpClient client, string? cacheDirectory = null, string? logDirectory = null)
    {
        GameDirectory = Path.GetFullPath(gameDirectory);
        CacheDirectory = Path.GetFullPath(cacheDirectory ?? LinuxPaths.NativesCache);
        LogDirectory = Path.GetFullPath(logDirectory ?? LinuxPaths.LogsHome);
        _downloads = new VerifiedDownloads(client);
    }

    /// <summary>用户选择的游戏目录（versions / assets / libraries / instances 均在此目录下）。</summary>
    public string GameDirectory { get; }

    /// <summary>启动器缓存目录，原生库解压到其下的 <c>&lt;hex-guid&gt;</c> 子目录。默认 <see cref="LinuxPaths.NativesCache"/>。</summary>
    public string CacheDirectory { get; }

    /// <summary>启动器写出的游戏运行日志目录。默认 <see cref="LinuxPaths.LogsHome"/>。</summary>
    public string LogDirectory { get; }

    private readonly VerifiedDownloads _downloads;
    private IReadOnlyList<GameRelease>? _catalog;
    private VersionResolver Resolver => new(GameDirectory, FetchMetadataAsync);

    public async Task<IReadOnlyList<GameRelease>> GetVersionsAsync(CancellationToken token)
    {
        if (_catalog is not null) return _catalog;
        var json = JsonNode.Parse(await _downloads.FetchTextAsync(ManifestUrl, token))!;
        _catalog = json["versions"]!.AsArray().OfType<JsonObject>()
            .Select(version => new GameRelease(version["id"]!.GetValue<string>(), version["type"]!.GetValue<string>(),
                version["url"]!.GetValue<string>(), version["sha1"]!.GetValue<string>())).ToArray();
        return _catalog;
    }

    public async Task<IReadOnlyList<GameRelease>> GetReleasesAsync(CancellationToken token) =>
        (await GetVersionsAsync(token)).Where(version => version.Type == "release").ToArray();

    public IReadOnlyList<string> GetInstalledVersions()
    {
        var directory = Path.Combine(GameDirectory, "versions");
        return !Directory.Exists(directory) ? [] : Directory.EnumerateDirectories(directory)
            .Where(path => File.Exists(Path.Combine(path, Path.GetFileName(path) + ".json")))
            .Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.Ordinal).ToArray();
    }

    private async Task FetchMetadataAsync(string id, CancellationToken token)
    {
        var release = (await GetVersionsAsync(token)).FirstOrDefault(version => version.Id == id)
                      ?? throw new FileNotFoundException($"缺少继承版本 {id}。请先导入或安装该版本。");
        await _downloads.FetchAsync(release.Url, Resolver.MetadataPath(id), release.Sha1, token);
    }

    public async Task InstallAsync(GameRelease release, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report($"正在下载 {release.Id} 的版本信息…");
        await _downloads.FetchAsync(release.Url, Resolver.MetadataPath(release.Id), release.Sha1, token);
        await CompleteFilesAsync(release.Id, progress, token);
    }

    public async Task InstallMinecraftAsync(string id, IProgress<string>? progress, CancellationToken token)
    {
        if (!File.Exists(Resolver.MetadataPath(id))) await FetchMetadataAsync(id, token);
        await CompleteFilesAsync(id, progress, token);
    }

    public Task<ResolvedVersion> ResolveAsync(string id, CancellationToken token) => Resolver.ResolveAsync(id, token);

    public async Task<int> GetRequiredJavaVersionAsync(string id, CancellationToken token) =>
        (await ResolveAsync(id, token)).Metadata["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;

    public async Task<string> GetInstanceDirectoryAsync(string id, CancellationToken token)
    {
        var profile = (await ResolveAsync(id, token)).Metadata;
        return profile["pclGameDirectory"] is { } path ? LinuxRules.ResolvePath(GameDirectory, path.GetValue<string>()) : GameDirectory;
    }

    public async Task CreateInstanceAsync(string source, string id, CancellationToken token)
    {
        _ = await ResolveAsync(source, token);
        var path = Resolver.MetadataPath(id);
        if (File.Exists(path)) throw new IOException($"实例 {id} 已存在。");
        var metadata = new JsonObject { ["id"] = id, ["inheritsFrom"] = source, ["pclGameDirectory"] = $"instances/{id}" };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, metadata.ToJsonString(), token);
        Directory.CreateDirectory(LinuxRules.ResolvePath(GameDirectory, $"instances/{id}"));
    }

    public IEnumerable<LibraryDownload> Libraries(JsonObject version)
    {
        foreach (var library in (version["libraries"] as JsonArray ?? []).OfType<JsonObject>().Where(lib => LinuxRules.Allows(lib["rules"])))
        {
            if (library["downloads"]?["artifact"] is { } artifact) yield return Describe(artifact, library, false);
            else if (library["name"] is not null && library["downloads"] is null) yield return Describe(null, library, false);
            if (library["natives"]?["linux"] is not { } nativeName) continue;
            var classifier = nativeName.GetValue<string>().Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
            var native = library["downloads"]?["classifiers"]?[classifier];
            yield return native is not null ? Describe(native, library, true) : Describe(null, library, true, classifier);
        }
    }

    private LibraryDownload Describe(JsonNode? descriptor, JsonObject library, bool native, string? classifier = null)
    {
        var name = library["name"]?.GetValue<string>();
        var relative = descriptor?["path"]?.GetValue<string>() ?? PortableMaven.PathOf(name + (classifier is null ? "" : ":" + classifier));
        var url = descriptor is null ? (library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + relative
            : descriptor["url"]?.GetValue<string>();
        return new(LinuxRules.ResolvePath(Path.Combine(GameDirectory, "libraries"), relative), UpgradeHttps(url), descriptor?["sha1"]?.GetValue<string>(), native);
    }

    public async Task CompleteFilesAsync(string id, IProgress<string>? progress, CancellationToken token)
    {
        var resolved = await ResolveAsync(id, token);
        var version = resolved.Metadata;
        var files = new Dictionary<string, (string Url, string? Hash)>(StringComparer.Ordinal);
        if (version["downloads"]?["client"] is { } game) files[Resolver.JarPath(resolved.JarId)] = Download(game);
        else if (!File.Exists(Resolver.JarPath(resolved.JarId))) throw new FileNotFoundException("找不到游戏客户端 JAR。");
        foreach (var library in Libraries(version))
        {
            if (!string.IsNullOrWhiteSpace(library.Url)) files[library.Path] = (library.Url, library.Hash);
            else if (!File.Exists(library.Path)) throw new FileNotFoundException($"缺少加载器生成的依赖库，请重新安装加载器：{Path.GetFileName(library.Path)}");
        }
        JsonNode? assetIndex = null;
        var indexInfo = version["assetIndex"];
        if (indexInfo is not null)
        {
            var indexPath = IndexPath(indexInfo["id"]!.GetValue<string>());
            progress?.Report("正在获取资源索引…");
            await _downloads.FetchAsync(indexInfo["url"]!.GetValue<string>(), indexPath, indexInfo["sha1"]?.GetValue<string>(), token);
            assetIndex = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, token))!;
            foreach (var asset in assetIndex["objects"]!.AsObject())
            {
                var hash = asset.Value!["hash"]!.GetValue<string>();
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("无效的资源 SHA-1。");
                files[AssetPath(hash)] = ($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", hash);
            }
        }
        if (version["logging"]?["client"]?["file"] is { } logging) files[LoggingPath(logging)] = Download(logging);
        var completed = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (file, ct) =>
        {
            await _downloads.FetchAsync(file.Value.Url, file.Key, file.Value.Hash, ct);
            var count = Interlocked.Increment(ref completed);
            if (count % 20 == 0 || count == files.Count) progress?.Report($"校验 / 下载文件：{count} / {files.Count}");
        });
        if (assetIndex is not null && indexInfo is not null)
        {
            var maps = new List<string>();
            if (assetIndex["virtual"]?.GetValue<bool>() == true)
                maps.Add(LinuxRules.ResolvePath(Path.Combine(GameDirectory, "assets", "virtual"), indexInfo["id"]!.GetValue<string>()));
            if (assetIndex["map_to_resources"]?.GetValue<bool>() == true)
                maps.Add(Path.Combine(await GetInstanceDirectoryAsync(id, token), "resources"));
            foreach (var directory in maps)
                foreach (var asset in assetIndex["objects"]!.AsObject())
                {
                    token.ThrowIfCancellationRequested();
                    var target = LinuxRules.ResolvePath(directory, asset.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(AssetPath(asset.Value!["hash"]!.GetValue<string>()), target, true);
                }
        }
    }

    public async Task<PreparedGame> PrepareAsync(string id, LaunchSettings settings, IProgress<string>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此启动入口仅用于 Linux。");
        if (settings.MemoryMb is < 512 or > 131072) throw new ArgumentOutOfRangeException(nameof(settings.MemoryMb));
        await CompleteFilesAsync(id, progress, token);
        var resolved = await ResolveAsync(id, token);
        var version = resolved.Metadata;
        var instance = settings.InstanceDirectory ?? await GetInstanceDirectoryAsync(id, token);
        Directory.CreateDirectory(instance);
        // 原生库属于启动器缓存，放在 XDG 缓存目录（默认 ~/.cache/pcl-ce/natives），不污染游戏目录。
        var natives = Path.Combine(CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(natives);
        try
        {
            var libraries = Libraries(version).ToArray();
            foreach (var library in libraries.Where(lib => lib.Native)) ExtractNatives(library.Path, natives);
            var classpath = string.Join(Path.PathSeparator, libraries.Where(lib => !lib.Native).Select(lib => lib.Path)
                .Append(Resolver.JarPath(resolved.JarId)).Distinct(StringComparer.Ordinal));
            var assetsId = version["assetIndex"]?["id"]?.GetValue<string>() ?? version["assets"]?.GetValue<string>() ?? "legacy";
            var variables = new Dictionary<string, string>
            {
                ["natives_directory"] = natives, ["launcher_name"] = "PCL-CE", ["launcher_version"] = "1.0.0",
                ["classpath"] = classpath, ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["library_directory"] = Path.Combine(GameDirectory, "libraries"),
                ["auth_player_name"] = settings.Account.Name, ["version_name"] = id,
                ["game_directory"] = instance, ["assets_root"] = Path.Combine(GameDirectory, "assets"),
                ["game_assets"] = LinuxRules.ResolvePath(Path.Combine(GameDirectory, "assets", "virtual"), assetsId),
                ["assets_index_name"] = assetsId, ["auth_uuid"] = settings.Account.Uuid,
                ["auth_access_token"] = settings.Account.AccessToken,
                ["auth_session"] = settings.Account.Type == "legacy" ? settings.Account.Name : $"token:{settings.Account.AccessToken}:{settings.Account.Uuid}",
                ["clientid"] = "", ["auth_xuid"] = "", ["user_type"] = settings.Account.Type,
                ["version_type"] = version["type"]?.GetValue<string>() ?? "release", ["user_properties"] = "{}"
            };
            var start = new ProcessStartInfo(settings.JavaPath)
            {
                WorkingDirectory = instance, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add($"-Xmx{settings.MemoryMb}M");
            if (version["arguments"]?["jvm"] is JsonArray jvm)
                foreach (var argument in LinuxRules.Arguments(jvm, variables))
                    // Forge's module scanner must ignore the actual base JAR even when an inherited
                    // profile has a different id. Official profiles use ${version_name}.jar here.
                    start.ArgumentList.Add(argument.StartsWith("-DignoreList=", StringComparison.Ordinal)
                        ? argument + "," + Path.GetFileName(Resolver.JarPath(resolved.JarId)) : argument);
            else
            {
                start.ArgumentList.Add("-Djava.library.path=" + natives);
                start.ArgumentList.Add("-cp"); start.ArgumentList.Add(classpath);
            }
            foreach (var argument in PortableArguments.Split(version["pclJvmArguments"]?.ToString() ?? "")) start.ArgumentList.Add(argument);
            foreach (var argument in PortableArguments.Split(settings.ExtraJvmArguments)) start.ArgumentList.Add(argument);
            if (version["logging"]?["client"] is { } logConfig)
                start.ArgumentList.Add(logConfig["argument"]!.GetValue<string>().Replace("${path}", LoggingPath(logConfig["file"]!)));
            start.ArgumentList.Add(version["mainClass"]!.GetValue<string>());
            var gameArgs = version["arguments"]?["game"] as JsonArray ?? new JsonArray(
                PortableArguments.Split(version["minecraftArguments"]?.GetValue<string>() ?? "${auth_player_name} ${auth_session}")
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            foreach (var argument in LinuxRules.Arguments(gameArgs, variables)) start.ArgumentList.Add(argument);
            foreach (var argument in PortableArguments.Split(version["pclGameArguments"]?.ToString() ?? "")) start.ArgumentList.Add(argument);
            foreach (var argument in PortableArguments.Split(settings.ExtraGameArguments)) start.ArgumentList.Add(argument);
            // 运行日志属于启动器状态数据，放在 XDG 状态目录（默认 ~/.local/state/pcl-ce/logs），
            // 而不是用户游戏目录/实例目录里，避免污染存档与整合包导出。
            Directory.CreateDirectory(LogDirectory);
            CleanupGameLogs();
            var logPath = Path.Combine(LogDirectory,
                $"game-{id}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.log");
            return new(start, logPath,
                version["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8, natives);
        }
        catch { Directory.Delete(natives, true); throw; }
    }

    /// <summary>清理过旧的运行日志：删除超过 14 天的日志，并只保留最新的 30 个。尽力而为，从不抛出。</summary>
    private void CleanupGameLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var files = new DirectoryInfo(LogDirectory).EnumerateFiles("game-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            var cutoff = DateTime.UtcNow.AddDays(-MaxLogAgeDays);
            for (var index = 0; index < files.Length; index++)
            {
                if (index < MaxLogFiles && files[index].LastWriteTimeUtc >= cutoff) continue;
                try { files[index].Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { }
    }

    public static void ExtractNatives(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0 || !entry.Name.Contains(".so", StringComparison.Ordinal)) continue;
            var path = LinuxRules.ResolvePath(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    private static (string Url, string? Hash) Download(JsonNode node) => (UpgradeHttps(node["url"]!.GetValue<string>())!, node["sha1"]?.GetValue<string>());
    private static string? UpgradeHttps(string? url) => url?.StartsWith("http://", StringComparison.Ordinal) == true ? "https://" + url[7..] : url;
    private string AssetPath(string hash) => Path.Combine(GameDirectory, "assets", "objects", hash[..2], hash);
    private string IndexPath(string id) => LinuxRules.ResolvePath(Path.Combine(GameDirectory, "assets", "indexes"), id + ".json");
    private string LoggingPath(JsonNode node) => LinuxRules.ResolvePath(Path.Combine(GameDirectory, "assets", "log_configs"), node["id"]!.GetValue<string>());
}
