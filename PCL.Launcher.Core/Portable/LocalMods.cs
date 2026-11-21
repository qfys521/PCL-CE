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
    public static IReadOnlyList<LocalMod> List(string instance)
    {
        var directory = System.IO.Path.Combine(instance, "mods");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory).Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Select(Read).OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
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
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException) { version = "元数据无法读取"; }
        return new(path, name, version, !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Match(string text, string key)
    {
        var match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static void Toggle(LocalMod mod) => File.Move(mod.Path, mod.Enabled ? mod.Path + ".disabled" : mod.Path[..^9], false);

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
        var destination = LinuxRules.ResolvePath(directory, System.IO.Path.GetFileName(source));
        if (System.IO.Path.GetFullPath(source) == destination) return;
        if (File.Exists(destination)) Remove(Read(destination));
        File.Copy(source, destination);
    }
}
