using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCL.Portable;

public enum LoaderKind { Vanilla, Fabric, Forge, NeoForge, Quilt }

public sealed class LoaderInstaller(LinuxGameLauncher launcher, HttpClient client)
{
    private readonly VerifiedDownloads _downloads = new(client);
    private const string ForgeRepository = "https://maven.minecraftforge.net/";
    private const string NeoRepository = "https://maven.neoforged.net/releases/";

    public async Task<IReadOnlyList<string>> GetVersionsAsync(LoaderKind kind, string minecraft, CancellationToken token)
    {
        if (kind == LoaderKind.Vanilla) return [];
        if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
        {
            var api = kind == LoaderKind.Fabric ? "https://meta.fabricmc.net/v2" : "https://meta.quiltmc.org/v3";
            var json = JsonNode.Parse(await client.GetStringAsync($"{api}/versions/loader/{Uri.EscapeDataString(minecraft)}", token))!;
            return json.AsArray().Select(node => node!["loader"]!["version"]!.GetValue<string>()).Distinct()
                .OrderByDescending(version => Version.TryParse(version.Split('-', '+')[0], out var numeric) ? numeric : new Version(0, 0))
                .ThenBy(version => version.Contains('-')).ThenByDescending(version => version, StringComparer.Ordinal).ToArray();
        }
        var repo = kind == LoaderKind.Forge ? ForgeRepository + "net/minecraftforge/forge/" : NeoRepository + "net/neoforged/neoforge/";
        var xml = XDocument.Parse(await client.GetStringAsync(repo + "maven-metadata.xml", token));
        var versions = xml.Descendants("version").Select(node => node.Value);
        if (kind == LoaderKind.Forge)
            return versions.Where(version => version.StartsWith(minecraft + "-", StringComparison.Ordinal))
                .Select(version => version[(minecraft.Length + 1)..]).Reverse().ToArray();
        var mc = minecraft.Split('.');
        var prefix = mc.Length >= 2 && mc[0] == "1" ? mc[1] + "." + (mc.Length > 2 ? mc[2] : "0") + "." : minecraft + ".";
        return versions.Where(version => version.StartsWith(prefix, StringComparison.Ordinal)).Reverse().ToArray();
    }

    public async Task<string> InstallAsync(LoaderKind kind, string minecraft, string? loaderVersion, string javaPath,
        IProgress<string>? progress, CancellationToken token)
    {
        await launcher.InstallMinecraftAsync(minecraft, progress, token);
        if (kind == LoaderKind.Vanilla) return minecraft;
        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? (await GetVersionsAsync(kind, minecraft, token)).FirstOrDefault() : loaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion)) throw new InvalidOperationException($"未找到适用于 {minecraft} 的 {kind} 版本。");
        if (!Regex.IsMatch(loaderVersion, @"^[A-Za-z0-9_.+\-]+$")) throw new InvalidDataException("无效的加载器版本号。");
        JsonObject profile;
        if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
        {
            progress?.Report($"正在安装 {kind} {loaderVersion}…");
            var api = kind == LoaderKind.Fabric ? "https://meta.fabricmc.net/v2" : "https://meta.quiltmc.org/v3";
            profile = JsonNode.Parse(await client.GetStringAsync($"{api}/versions/loader/{Uri.EscapeDataString(minecraft)}/{Uri.EscapeDataString(loaderVersion)}/profile/json", token))!.AsObject();
        }
        else
        {
            var coordinate = kind == LoaderKind.Forge ? $"net.minecraftforge:forge:{minecraft}-{loaderVersion}:installer" : $"net.neoforged:neoforge:{loaderVersion}:installer";
            var repository = kind == LoaderKind.Forge ? ForgeRepository : NeoRepository;
            var installer = LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, ".pcl-installers"), PortableMaven.PathOf(coordinate));
            var url = repository + PortableMaven.PathOf(coordinate);
            string? digest = null;
            try { digest = (await client.GetStringAsync(url + ".sha1", token)).Split(' ', '\n', '\r')[0].Trim(); }
            catch (HttpRequestException) { }
            progress?.Report($"正在下载 {kind} 安装器…");
            await _downloads.FetchAsync(url, installer, digest, token);
            profile = await InstallForgeArchiveAsync(installer, minecraft, javaPath, progress, token);
        }
        var id = VersionResolver.ValidateId(profile["id"]!.GetValue<string>());
        profile["pclGameDirectory"] = $"instances/{id}";
        var metadata = new VersionResolver(launcher.GameDirectory).MetadataPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        var previous = File.Exists(metadata) ? await File.ReadAllTextAsync(metadata, token) : null;
        await File.WriteAllTextAsync(metadata, profile.ToJsonString(), token);
        try { await launcher.CompleteFilesAsync(id, progress, token); }
        catch
        {
            if (previous is not null) await File.WriteAllTextAsync(metadata, previous, CancellationToken.None);
            else File.Delete(metadata);
            throw;
        }
        return id;
    }

    public async Task<JsonObject> InstallForgeArchiveAsync(string installer, string minecraft, string javaPath,
        IProgress<string>? progress, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(installer);
        JsonObject ReadJson(string name)
        {
            using var stream = archive.GetEntry(name)?.Open() ?? throw new InvalidDataException($"安装器缺少 {name}");
            return JsonNode.Parse(stream)!.AsObject();
        }
        var installation = ReadJson("install_profile.json");
        var version = archive.GetEntry("version.json") is not null ? ReadJson("version.json")
            : installation["versionInfo"]?.DeepClone().AsObject() ?? throw new InvalidDataException("无法识别 Forge 安装信息。");
        version["inheritsFrom"] ??= minecraft;
        var work = Path.Combine(launcher.GameDirectory, ".pcl-installers", "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0) continue;
                if (entry.FullName.StartsWith("maven/", StringComparison.Ordinal))
                    Extract(entry, LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, "libraries"), entry.FullName[6..]));
                else if (entry.FullName.StartsWith("data/", StringComparison.Ordinal)) Extract(entry, LinuxRules.ResolvePath(work, entry.FullName));
            }
            // Legacy installers bundle the universal JAR directly rather than under maven/.
            if (installation["install"] is { } legacy && legacy["path"] is { } coordinate && legacy["filePath"] is { } file)
            {
                var entry = archive.GetEntry(file.GetValue<string>()) ?? throw new InvalidDataException("Forge 安装器没有内嵌的通用 JAR。");
                Extract(entry, LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, "libraries"), PortableMaven.PathOf(coordinate.GetValue<string>())));
            }
            foreach (var library in launcher.Libraries(installation))
            {
                if (string.IsNullOrWhiteSpace(library.Url))
                {
                    if (!File.Exists(library.Path)) throw new FileNotFoundException(library.Path);
                }
                else await _downloads.FetchAsync(library.Url, library.Path, library.Hash, token);
            }
            var variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SIDE"] = "client", ["MINECRAFT_VERSION"] = minecraft,
                ["MINECRAFT_JAR"] = new VersionResolver(launcher.GameDirectory).JarPath(minecraft),
                ["ROOT"] = launcher.GameDirectory, ["LIBRARY_DIR"] = Path.Combine(launcher.GameDirectory, "libraries"),
                ["INSTALLER"] = Path.GetFullPath(installer)
            };
            foreach (var data in installation["data"]?.AsObject() ?? new JsonObject())
            {
                var value = data.Value?["client"]?.GetValue<string>();
                if (value is not null) variables[data.Key] = value;
            }
            string Expand(string value, int depth = 0)
            {
                if (depth > 16) throw new InvalidDataException("Forge 安装参数存在循环引用。");
                value = Regex.Replace(value, @"\{([A-Za-z0-9_]+)\}", match => variables.TryGetValue(match.Groups[1].Value, out var replacement)
                    ? Expand(replacement, depth + 1) : throw new InvalidDataException($"未知安装参数 {match.Value}"));
                value = Regex.Replace(value, @"\[([^\[\]]+)\]", match => LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, "libraries"), PortableMaven.PathOf(match.Groups[1].Value)));
                if (value.StartsWith("/data/", StringComparison.Ordinal)) value = LinuxRules.ResolvePath(work, value[1..]);
                return value.Trim('\'');
            }
            var processors = (installation["processors"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(processor => processor["sides"] is not JsonArray sides || sides.Any(side => side!.GetValue<string>() == "client")).ToArray();
            for (var i = 0; i < processors.Length; i++)
            {
                var processor = processors[i];
                progress?.Report($"正在处理 Forge / NeoForge 客户端：{i + 1} / {processors.Length}…");
                var outputs = processor["outputs"]?.AsObject().Select(pair => (Path: Expand(pair.Key), Hash: Expand(pair.Value!.GetValue<string>()))).ToArray() ?? [];
                if (outputs.Length > 0)
                {
                    var valid = true;
                    foreach (var output in outputs) valid &= await VerifiedDownloads.IsValidAsync(output.Path, output.Hash, token);
                    if (valid) continue;
                }
                var jar = LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, "libraries"), PortableMaven.PathOf(processor["jar"]!.GetValue<string>()));
                var classpath = new[] { jar }.Concat((processor["classpath"] as JsonArray ?? [])
                    .Select(node => LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, "libraries"), PortableMaven.PathOf(node!.GetValue<string>()))));
                var start = new ProcessStartInfo(javaPath) { WorkingDirectory = launcher.GameDirectory };
                start.ArgumentList.Add("-cp"); start.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
                start.ArgumentList.Add(ReadMainClass(jar));
                foreach (var arg in processor["args"]!.AsArray()) start.ArgumentList.Add(Expand(arg!.GetValue<string>()));
                var result = await PortableProcess.RunAsync(start, token);
                if (result.ExitCode != 0) throw new IOException($"加载器安装步骤失败（{result.ExitCode}）：\n{result.Output}");
                foreach (var output in outputs)
                    if (!await VerifiedDownloads.IsValidAsync(output.Path, output.Hash, token)) throw new InvalidDataException($"安装器生成文件校验失败：{output.Path}");
            }
            return version;
        }
        finally { Directory.Delete(work, true); }
    }

    private static void Extract(ZipArchiveEntry entry, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        entry.ExtractToFile(path, true);
    }

    public static string ReadMainClass(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        using var stream = zip.GetEntry("META-INF/MANIFEST.MF")?.Open() ?? throw new InvalidDataException("安装工具没有 JAR 清单。");
        using var reader = new StreamReader(stream);
        var manifest = reader.ReadToEnd().Replace("\r\n ", "").Replace("\n ", "");
        var match = Regex.Match(manifest, @"(?m)^Main-Class:\s*([^\r\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : throw new InvalidDataException("安装工具没有 Main-Class。");
    }
}
