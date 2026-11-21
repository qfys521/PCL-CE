using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Portable;

public sealed record LocalMod(string Path, string Name, string Version, bool Enabled)
{
    public override string ToString() => $"{(Enabled ? "✓" : "○")}  {Name}  {Version}\n{System.IO.Path.GetFileName(Path)}";
}

public static class LocalMods
{
    private const string UnreadableVersion = "元数据无法读取";
    private const string DisabledSuffix = ".disabled";

    public static IReadOnlyList<LocalMod> List(string instance)
    {
        var directory = System.IO.Path.Combine(instance, "mods");
        if (!Directory.Exists(directory)) return [];
        var mods = Directory.EnumerateFiles(directory)
            .Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Select(Read).ToArray();
        // ext4 / btrfs 区分大小写：仅大小写不同的条目视为同一个模组，优先保留启用且有结构化元数据的那个。
        return mods
            .GroupBy(mod => BaseName(mod.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(mod => mod.Enabled)
                .ThenByDescending(mod => !string.Equals(mod.Version, UnreadableVersion, StringComparison.Ordinal))
                .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.Path, StringComparer.Ordinal)
                .First())
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => System.IO.Path.GetFileName(mod.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BaseName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^DisabledSuffix.Length] : name;
    }

    /// <summary>
    /// 在目录中按大小写不敏感方式查找文件名：Linux 上 <c>JEI.jar</c> 与 <c>jei.jar</c> 是不同文件，
    /// 因此下载、导入、切换、删除前都要先复用已存在的同名（仅大小写不同）文件，避免产生重复文件。
    /// 找不到时返回 <c>null</c>。
    /// </summary>
    public static string? ResolveExistingCaseInsensitive(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory)) return null;
        var direct = System.IO.Path.Combine(directory, fileName);
        if (File.Exists(direct)) return direct;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            if (string.Equals(System.IO.Path.GetFileName(entry), fileName, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    /// <summary>
    /// 解析远程模组 / 资源包 / 光影的写入目标：优先复用仅大小写不同的现有文件；
    /// 若只存在禁用变体，则先移入回收站，再返回规范路径（导入后为启用状态）。路径始终限定在目录内。
    /// </summary>
    public static string ResolveContentTarget(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        if (ResolveExistingCaseInsensitive(directory, fileName) is { } existing) return existing;
        if (ResolveExistingCaseInsensitive(directory, fileName + DisabledSuffix) is { } disabled) Remove(Read(disabled));
        return LinuxRules.ResolvePath(directory, fileName);
    }

    private static LocalMod Read(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var version = "";
        try
        {
            using var archive = ZipFile.OpenRead(path);
            using var fabric = archive.GetEntry("fabric.mod.json")?.Open();
            if (fabric is not null)
            {
                var node = JsonNode.Parse(fabric)!;
                name = node["name"]?.ToString() ?? node["id"]?.ToString() ?? name;
                version = node["version"]?.ToString() ?? "";
            }
            else
            {
                var entry = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
                if (entry is not null)
                {
                    using var reader = new StreamReader(entry.Open());
                    var text = reader.ReadToEnd();
                    name = Match(text, "displayName") ?? Match(text, "modId") ?? name;
                    version = Match(text, "version") ?? "";
                    if (version.Contains("${", StringComparison.Ordinal)) version = "";
                }
                else if (archive.GetEntry("mcmod.info") is { } info)
                {
                    using var stream = info.Open();
                    var json = JsonNode.Parse(stream);
                    var mod = json is JsonArray array ? array.FirstOrDefault() : json?["modList"]?.AsArray().FirstOrDefault();
                    name = mod?["name"]?.ToString() ?? name;
                    version = mod?["version"]?.ToString() ?? "";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException) { version = UnreadableVersion; }
        return new(path, name, version, !path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Match(string text, string key)
    {
        var match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static void Toggle(LocalMod mod)
    {
        var directory = System.IO.Path.GetDirectoryName(mod.Path)!;
        var fileName = System.IO.Path.GetFileName(mod.Path);
        var targetName = mod.Enabled
            ? fileName + DisabledSuffix
            : fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;
        var target = ResolveExistingCaseInsensitive(directory, targetName) ?? System.IO.Path.Combine(directory, targetName);
        if (string.Equals(target, mod.Path, StringComparison.Ordinal)) return;
        if (File.Exists(target)) Remove(Read(target));
        File.Move(mod.Path, target, false);
    }

    public static void Remove(LocalMod mod)
    {
        var trash = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(mod.Path)!, ".pcl-trash");
        Directory.CreateDirectory(trash);
        File.Move(mod.Path, System.IO.Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + System.IO.Path.GetFileName(mod.Path)));
    }

    public static void Import(string source, string instance)
    {
        if (!source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 JAR 模组文件。");
        using (ZipFile.OpenRead(source)) { }
        var directory = System.IO.Path.Combine(instance, "mods");
        Directory.CreateDirectory(directory);
        var sourcePath = System.IO.Path.GetFullPath(source);
        var destination = ResolveContentTarget(directory, System.IO.Path.GetFileName(sourcePath));
        if (string.Equals(sourcePath, destination, StringComparison.Ordinal)) return;
        if (File.Exists(destination)) Remove(Read(destination));
        File.Copy(sourcePath, destination);
        // 从 mods 目录内导入仅大小写不同的同一文件时，删除旧的重复项，保证最终只有一个文件。
        if (string.Equals(System.IO.Path.GetDirectoryName(sourcePath), System.IO.Path.GetDirectoryName(destination), StringComparison.Ordinal) &&
            string.Equals(BaseName(sourcePath), BaseName(destination), StringComparison.OrdinalIgnoreCase) && File.Exists(sourcePath))
        {
            try { File.Delete(sourcePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
