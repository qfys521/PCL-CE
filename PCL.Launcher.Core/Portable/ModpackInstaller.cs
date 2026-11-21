using System.IO.Compression;
using System.Text.Json.Nodes;

namespace PCL.Portable;

public sealed record PackPlan(string Name, string Minecraft, LoaderKind Loader, string? LoaderVersion, string Format,
    string Prefix, JsonObject Manifest);

/// <summary>Imports client packs into new isolated instances; failed imports never replace an existing instance.</summary>
public sealed class ModpackInstaller(LinuxGameLauncher launcher, HttpClient client, string? curseForgeKey = null)
{
    public static PackPlan Inspect(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => entry.Name.Length > 0).ToArray();
        foreach (var name in new[] { "modrinth.index.json", "manifest.json", "mmc-pack.json", "mcbbs.packmeta", "modpack.json" })
        {
            var entry = entries.FirstOrDefault(entry => entry.FullName == name || entry.FullName.EndsWith("/" + name, StringComparison.Ordinal));
            if (entry is null) continue;
            var prefix = entry.FullName[..^name.Length];
            using var stream = entry.Open();
            var json = JsonNode.Parse(stream)!.AsObject();
            var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (name)
            {
                case "modrinth.index.json":
                    if (json["game"]?.ToString() != "minecraft") throw new InvalidDataException("此整合包不适用于 Minecraft。");
                    foreach (var pair in json["dependencies"]!.AsObject()) dependencies[pair.Key] = pair.Value!.ToString();
                    break;
                case "manifest.json" when json["minecraft"] is not null:
                    dependencies["minecraft"] = json["minecraft"]!["version"]!.ToString();
                    foreach (var loader in json["minecraft"]!["modLoaders"]!.AsArray())
                    {
                        var parts = loader!["id"]!.ToString().Split('-', 2);
                        dependencies[parts[0] + (parts[0] == "fabric" ? "-loader" : "")] = parts[1];
                    }
                    break;
                case "mmc-pack.json":
                    foreach (var component in json["components"]!.AsArray())
                    {
                        var key = component!["uid"]!.ToString() switch
                        {
                            "net.minecraft" => "minecraft", "net.minecraftforge" => "forge",
                            "net.neoforged" or "net.neoforged.neoforge" => "neoforge",
                            "net.fabricmc.fabric-loader" => "fabric-loader", "org.quiltmc.quilt-loader" => "quilt-loader", _ => ""
                        };
                        if (key.Length > 0) dependencies[key] = component["version"]!.ToString();
                    }
                    break;
                case "mcbbs.packmeta":
                    foreach (var addon in json["addons"]!.AsArray())
                    {
                        var key = addon!["id"]!.ToString();
                        dependencies[key switch { "game" => "minecraft", "fabric" => "fabric-loader", "quilt" => "quilt-loader", _ => key }] = addon["version"]!.ToString();
                    }
                    break;
                case "modpack.json" when json["gameVersion"] is not null:
                    dependencies["minecraft"] = json["gameVersion"]!.ToString();
                    break;
                default: continue;
            }
            if (!dependencies.TryGetValue("minecraft", out var minecraft)) throw new InvalidDataException("整合包没有指定 Minecraft 版本。");
            var loaders = new[] { ("fabric-loader", LoaderKind.Fabric), ("forge", LoaderKind.Forge), ("neoforge", LoaderKind.NeoForge), ("quilt-loader", LoaderKind.Quilt) }
                .Where(pair => dependencies.ContainsKey(pair.Item1)).ToArray();
            if (loaders.Length > 1) throw new InvalidDataException("整合包指定了多个互不兼容的模组加载器。");
            var selected = loaders.FirstOrDefault();
            var version = loaders.Length > 0 ? dependencies[selected.Item1] : null;
            if (selected.Item2 == LoaderKind.Forge && version?.StartsWith(minecraft + "-", StringComparison.Ordinal) == true) version = version[(minecraft.Length + 1)..];
            return new(json["name"]?.ToString() ?? System.IO.Path.GetFileNameWithoutExtension(path), minecraft,
                loaders.Length > 0 ? selected.Item2 : LoaderKind.Vanilla, version, name, prefix, json);
        }
        // Older launcher packs are complete .minecraft directories without a manifest.
        var candidates = entries.Where(entry => entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Entry: entry, Parts: entry.FullName.Split('/')))
            .Where(item => item.Parts.Length >= 3 && item.Parts[^3] == "versions" && item.Parts[^1] == item.Parts[^2] + ".json")
            .ToArray();
        if (candidates.Length > 0)
        {
            var profiles = candidates.ToDictionary(item => item.Parts[^2], item =>
            {
                using var stream = item.Entry.Open(); return JsonNode.Parse(stream)!.AsObject();
            });
            var parents = profiles.Values.Select(profile => profile["inheritsFrom"]?.ToString()).OfType<string>().ToHashSet();
            var leaves = profiles.Keys.Where(id => !parents.Contains(id)).ToArray();
            if (leaves.Length != 1) throw new InvalidDataException("目录整合包包含多个独立游戏版本，请分别打包后导入。");
            var leaf = leaves[0]; var current = leaf; var visited = new HashSet<string>();
            while (profiles.TryGetValue(current, out var profile) && profile["inheritsFrom"] is { } parent)
            {
                if (!visited.Add(current)) throw new InvalidDataException("整合包包含循环继承。");
                current = parent.ToString();
            }
            var minecraft = profiles.GetValueOrDefault(current)?["clientVersion"]?.ToString() ?? current;
            var selected = candidates.Single(item => item.Parts[^2] == leaf);
            var prefix = string.Join('/', selected.Parts[..^3]);
            if (prefix.Length > 0) prefix += "/";
            return new(Path.GetFileNameWithoutExtension(path), minecraft, LoaderKind.Vanilla, null, "bundled", prefix,
                new JsonObject { ["profile"] = leaf });
        }
        throw new InvalidDataException("无法识别整合包。支持 Modrinth、CurseForge、MultiMC/Prism、MCBBS、HMCL 和包含 versions 的目录整合包。");
    }

    public async Task<string> ImportAsync(string archivePath, string instanceId, string javaPath,
        IProgress<string>? progress, CancellationToken token)
    {
        VersionResolver.ValidateId(instanceId);
        var metadataPath = new VersionResolver(launcher.GameDirectory).MetadataPath(instanceId);
        var destination = LinuxRules.ResolvePath(launcher.GameDirectory, "instances/" + instanceId);
        if (File.Exists(metadataPath) || Directory.Exists(destination)) throw new IOException($"实例 {instanceId} 已存在。请使用其他名称。");
        var plan = Inspect(archivePath);
        if (plan.Format == "bundled") return await ImportBundledAsync(archivePath, instanceId, plan, progress, token);
        var stage = LinuxRules.ResolvePath(launcher.GameDirectory, "instances/.import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var committed = false;
        try
        {
            if (plan.Format == "modrinth.index.json")
            {
                var downloads = new VerifiedDownloads(client);
                var files = plan.Manifest["files"]!.AsArray().Where(file => file!["env"]?["client"]?.ToString() != "unsupported").ToArray();
                for (var i = 0; i < files.Length; i++)
                {
                    var file = files[i]!;
                    progress?.Report($"正在下载整合包文件：{i + 1} / {files.Length}");
                    var target = LinuxRules.ResolvePath(stage, file["path"]!.ToString());
                    var hash = file["hashes"]?["sha512"]?.ToString() ?? file["hashes"]?["sha1"]?.ToString();
                    var urls = file["downloads"]!.AsArray().Select(node => node!.ToString()).ToArray();
                    if (urls.Length == 0) throw new InvalidDataException("整合包文件缺少下载地址。");
                    Exception? failure = null;
                    foreach (var url in urls)
                    {
                        try { await downloads.FetchAsync(url, target, hash, token); failure = null; break; }
                        catch (Exception ex) when (ex is IOException or HttpRequestException) { failure = ex; }
                    }
                    if (failure is not null) throw new IOException($"无法下载整合包文件 {file["path"]}", failure);
                }
            }
            else if (plan.Format == "manifest.json")
            {
                var catalog = new ContentCatalog(client, curseForgeKey);
                var files = plan.Manifest["files"]!.AsArray();
                for (var i = 0; i < files.Count; i++)
                {
                    progress?.Report($"正在下载 CurseForge 文件：{i + 1} / {files.Count}");
                    var file = files[i]!;
                    var version = await catalog.VersionAsync(CatalogSource.CurseForge, file["fileID"]!.ToString(), file["projectID"]!.ToString(), token);
                    if (version.Files.Count == 0)
                    {
                        if (file["required"]?.GetValue<bool>() == false) continue;
                        throw new InvalidOperationException($"作者禁止了 {version.Name} 的第三方下载，请从 CurseForge 下载允许外部启动器使用的整合包。");
                    }
                    foreach (var asset in version.Files)
                        await new VerifiedDownloads(client).FetchAsync(asset.Url,
                            LocalMods.ResolveContentTarget(Path.Combine(stage, asset.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : "resourcepacks"), asset.Name),
                            asset.Hash, token);
                }
            }
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var overrides = plan.Format switch
                {
                    "modrinth.index.json" => new[] { "overrides/", "client-overrides/" },
                    "manifest.json" => new[] { (plan.Manifest["overrides"]?.ToString() ?? "overrides").TrimEnd('/') + "/" },
                    "mmc-pack.json" => new[] { ".minecraft/", "minecraft/" },
                    "modpack.json" => new[] { "minecraft/" },
                    _ => new[] { "overrides/" }
                };
                foreach (var prefix in overrides)
                    ExtractPrefix(archive, plan.Prefix + prefix, stage, token);
            }
            var source = await new LoaderInstaller(launcher, client).InstallAsync(plan.Loader, plan.Minecraft, plan.LoaderVersion, javaPath, progress, token);
            var metadata = new JsonObject
            {
                ["id"] = instanceId, ["inheritsFrom"] = source, ["pclGameDirectory"] = "instances/" + instanceId,
                ["pclPackName"] = plan.Name, ["pclPackFormat"] = plan.Format
            };
            if (plan.Manifest["launchInfo"] is { } info)
            {
                metadata["pclJvmArguments"] = info["javaArgument"]?.DeepClone();
                metadata["pclGameArguments"] = info["launchArgument"]?.DeepClone();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            Directory.Move(stage, destination);
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), token);
            await launcher.CompleteFilesAsync(instanceId, progress, token);
            committed = true;
            return instanceId;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (!committed)
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                if (File.Exists(metadataPath)) File.Delete(metadataPath);
            }
        }
    }

    private async Task<string> ImportBundledAsync(string path, string id, PackPlan plan, IProgress<string>? progress, CancellationToken token)
    {
        var stage = Path.Combine(launcher.GameDirectory, "instances", ".import-" + Guid.NewGuid().ToString("N"));
        var destination = LinuxRules.ResolvePath(launcher.GameDirectory, "instances/" + id);
        var created = new List<string>(); var committed = false;
        Directory.CreateDirectory(stage);
        try
        {
            using (var archive = ZipFile.OpenRead(path)) ExtractPrefix(archive, plan.Prefix, stage, token);
            var sourceResolver = new VersionResolver(stage);
            var leaf = plan.Manifest["profile"]!.ToString();
            var sourceIds = Directory.GetDirectories(Path.Combine(stage, "versions"))
                .Select(Path.GetFileName).OfType<string>().Where(sourceId => File.Exists(sourceResolver.MetadataPath(sourceId))).ToArray();
            var names = sourceIds.ToDictionary(sourceId => sourceId, sourceId => sourceId == leaf ? id : VersionResolver.ValidateId(id + " - " + sourceId));
            var resolver = new VersionResolver(launcher.GameDirectory);
            foreach (var targetId in names.Values)
                if (Directory.Exists(Path.GetDirectoryName(resolver.MetadataPath(targetId)))) throw new IOException($"版本 {targetId} 已存在。");
            foreach (var sourceId in sourceIds)
            {
                token.ThrowIfCancellationRequested();
                var profile = JsonNode.Parse(await File.ReadAllTextAsync(sourceResolver.MetadataPath(sourceId), token))!.AsObject();
                if (profile["inheritsFrom"] is { } parent)
                {
                    if (names.TryGetValue(parent.ToString(), out var renamed)) profile["inheritsFrom"] = renamed;
                    else await launcher.InstallMinecraftAsync(parent.ToString(), progress, token);
                }
                if (profile["jar"] is { } jar && names.TryGetValue(jar.ToString(), out var renamedJar)) profile["jar"] = renamedJar;
                profile["id"] = names[sourceId]; profile["pclMinecraftVersion"] = plan.Minecraft;
                if (sourceId == leaf) { profile["pclGameDirectory"] = "instances/" + id; profile["pclPackFormat"] = "bundled"; }
                var targetPath = resolver.MetadataPath(names[sourceId]); var targetFolder = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetFolder); created.Add(targetFolder);
                await File.WriteAllTextAsync(targetPath, profile.ToJsonString(), token);
                if (File.Exists(sourceResolver.JarPath(sourceId))) File.Copy(sourceResolver.JarPath(sourceId), resolver.JarPath(names[sourceId]));
            }
            foreach (var cache in new[] { "libraries", "assets" })
            {
                var source = Path.Combine(stage, cache);
                if (!Directory.Exists(source)) continue;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    var target = LinuxRules.ResolvePath(Path.Combine(launcher.GameDirectory, cache), Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Copy(file, target);
                }
                Directory.Delete(source, true);
            }
            Directory.Delete(Path.Combine(stage, "versions"), true);
            Directory.Move(stage, destination);
            await launcher.CompleteFilesAsync(id, progress, token);
            committed = true; return id;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (!committed)
            {
                foreach (var folder in created) Directory.Delete(folder, true);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
            }
        }
    }

    public async Task ExportAsync(string id, string path, CancellationToken token)
    {
        var resolved = await launcher.ResolveAsync(id, token);
        var source = await launcher.GetInstanceDirectoryAsync(id, token);
        var dependencies = new JsonObject { ["minecraft"] = resolved.MinecraftVersion };
        foreach (var library in (resolved.Metadata["libraries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var parts = library["name"]?.ToString().Split(':');
            if (parts?.Length < 3 || parts is null) continue;
            var key = parts[0] switch
            {
                "net.fabricmc" when parts[1] == "fabric-loader" => "fabric-loader",
                "org.quiltmc" when parts[1] == "quilt-loader" => "quilt-loader",
                "net.minecraftforge" when parts[1] == "forge" => "forge",
                "net.neoforged" when parts[1] == "neoforge" => "neoforge", _ => null
            };
            if (key is not null) dependencies[key] = key == "forge" && parts[2].StartsWith(resolved.MinecraftVersion + "-", StringComparison.Ordinal)
                ? parts[2][(resolved.MinecraftVersion.Length + 1)..] : parts[2];
        }
        var manifest = new JsonObject
        {
            ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = "1.0.0", ["name"] = id,
            ["files"] = new JsonArray(), ["dependencies"] = dependencies
        };
        var temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("modrinth.index.json").Open()))
                    await writer.WriteAsync(manifest.ToJsonString().AsMemory(), token);
                if (Directory.Exists(source))
                    foreach (var file in EnumeratePackFiles(source))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Path.GetFullPath(file) == Path.GetFullPath(path) || Path.GetFullPath(file) == temporary) continue;
                        var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                        archive.CreateEntryFromFile(file, "overrides/" + relative, CompressionLevel.Fastest);
                    }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<string> EnumeratePackFiles(string source)
    {
        var skip = new HashSet<string>(["assets", "libraries", "versions", "instances", "logs", "crash-reports", "PCL", ".pcl-trash"], StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(); queue.Enqueue(source);
        while (queue.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || skip.Contains(name) || name.StartsWith(".pcl-", StringComparison.Ordinal)) continue;
                if (Directory.Exists(path)) queue.Enqueue(path);
                else if (!name.Contains("account", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase) && name != "servers.dat") yield return path;
            }
        }
    }

    public static void ExtractPrefix(ZipArchive archive, string prefix, string destination, CancellationToken token)
    {
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || entry.Name.Length == 0) continue;
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("整合包不能包含符号链接。");
            var target = LinuxRules.ResolvePath(destination, name[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }
}
