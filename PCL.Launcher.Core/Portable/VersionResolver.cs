using System.Text.Json.Nodes;

namespace PCL.Portable;

public sealed record ResolvedVersion(JsonObject Metadata, string JarId, string MinecraftVersion);

/// <summary>Resolves launcher profiles without modifying their parent or sharing mutable JSON nodes.</summary>
public sealed class VersionResolver(string root, Func<string, CancellationToken, Task>? fetchMissing = null)
{
    public Task<ResolvedVersion> ResolveAsync(string id, CancellationToken token) => ReadAsync(id, [], token);

    public string MetadataPath(string id) => LinuxRules.ResolvePath(Path.Combine(root, "versions"), $"{ValidateId(id)}/{id}.json");
    public string JarPath(string id) => LinuxRules.ResolvePath(Path.Combine(root, "versions"), $"{ValidateId(id)}/{id}.jar");

    public static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new InvalidDataException("版本名称不能包含路径分隔符或为空。");
        return id;
    }

    private async Task<ResolvedVersion> ReadAsync(string id, HashSet<string> visiting, CancellationToken token)
    {
        if (visiting.Count >= 32 || !visiting.Add(id)) throw new InvalidDataException("游戏版本存在循环或过深的继承关系。");
        var path = MetadataPath(id);
        if (!File.Exists(path) && fetchMissing is not null) await fetchMissing(id, token);
        var child = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        ResolvedVersion result;
        if (child["inheritsFrom"] is { } parentId)
        {
            var parent = await ReadAsync(parentId.GetValue<string>(), visiting, token);
            var merged = parent.Metadata.DeepClone().AsObject();
            foreach (var property in child)
            {
                switch (property.Key)
                {
                    case "libraries":
                        var libraries = (merged["libraries"] as JsonArray ?? []).Concat(property.Value!.AsArray());
                        // Child libraries replace older versions of the same artifact/classifier.
                        var ordered = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                        foreach (var library in libraries.OfType<JsonObject>())
                            ordered[PortableMaven.Identity(library["name"]!.GetValue<string>())] = library.DeepClone();
                        merged["libraries"] = new JsonArray(ordered.Values.ToArray());
                        break;
                    case "arguments":
                        var arguments = merged["arguments"]?.DeepClone().AsObject() ?? new JsonObject();
                        foreach (var kind in property.Value!.AsObject())
                        {
                            var values = (arguments[kind.Key] as JsonArray ?? []).Concat(kind.Value!.AsArray());
                            arguments[kind.Key] = new JsonArray(values.Select(value => value?.DeepClone()).ToArray());
                        }
                        merged["arguments"] = arguments;
                        break;
                    case "downloads":
                        var downloads = merged["downloads"]?.DeepClone().AsObject() ?? new JsonObject();
                        foreach (var file in property.Value!.AsObject()) downloads[file.Key] = file.Value?.DeepClone();
                        merged["downloads"] = downloads;
                        break;
                    default:
                        merged[property.Key] = property.Value?.DeepClone();
                        break;
                }
            }
            if (child["minecraftArguments"] is not null) merged.Remove("arguments");
            var jar = child["jar"]?.GetValue<string>() ?? (child["downloads"]?["client"] is not null ? id : parent.JarId);
            result = new(merged, ValidateId(jar), parent.MinecraftVersion);
        }
        else result = new(child.DeepClone().AsObject(), ValidateId(child["jar"]?.GetValue<string>() ?? id), child["pclMinecraftVersion"]?.ToString() ?? id);
        visiting.Remove(id);
        return result;
    }
}

public static class PortableMaven
{
    public static string PathOf(string coordinate)
    {
        var extensionParts = coordinate.Split('@', 2);
        var parts = extensionParts[0].Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException($"无效的 Maven 坐标：{coordinate}");
        var extension = extensionParts.Length > 1 ? extensionParts[1] : "jar";
        var classifier = parts.Length == 4 ? "-" + parts[3] : "";
        var relative = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.{extension}";
        // Validate coordinates as relative paths before using them for repositories or the filesystem.
        // Use the app-owned temp root as the (virtual) resolution base, never Path.GetTempPath().
        _ = LinuxRules.ResolvePath(LinuxPaths.TempHome, relative);
        return relative;
    }

    public static string Identity(string coordinate)
    {
        var parts = coordinate.Split(':');
        return parts.Length >= 3 ? $"{parts[0]}:{parts[1]}:{(parts.Length > 3 ? parts[3] : "")}" : coordinate;
    }
}

public static class PortableArguments
{
    /// <summary>Splits legacy/user argument strings. The resulting tokens never go through a shell.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        var value = new System.Text.StringBuilder();
        char quote = '\0';
        var escaped = false;
        var started = false;
        foreach (var ch in text)
        {
            if (escaped) { value.Append(ch); escaped = false; started = true; continue; }
            if (ch == '\\' && quote != '\'') { escaped = true; started = true; continue; }
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else value.Append(ch);
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; started = true; }
            else if (char.IsWhiteSpace(ch))
            {
                if (started) { result.Add(value.ToString()); value.Clear(); started = false; }
            }
            else { value.Append(ch); started = true; }
        }
        if (quote != '\0' || escaped) throw new FormatException("启动参数中的引号或转义未闭合。");
        if (started) result.Add(value.ToString());
        return result;
    }
}
