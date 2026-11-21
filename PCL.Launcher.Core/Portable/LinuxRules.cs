using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Portable;

/// <summary>Mojang's ordered allow/disallow rules, evaluated for the native Linux host.</summary>
public static class LinuxRules
{
    public static bool Allows(JsonNode? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is not JsonArray array) return true;
        var allowed = false;
        foreach (var rule in array.OfType<JsonObject>())
        {
            if (rule["os"] is JsonObject os)
            {
                if (os["name"] is { } name && name.GetValue<string>() != "linux") continue;
                if (os["arch"] is { } arch && !Regex.IsMatch(ArchitectureName, arch.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
                if (os["version"] is { } version && !Regex.IsMatch(Environment.OSVersion.VersionString, version.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
            }
            if (rule["features"] is JsonObject required && required.Any(pair =>
                    (features?.GetValueOrDefault(pair.Key) ?? false) != pair.Value!.GetValue<bool>())) continue;
            allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x86",
        _ => throw new PlatformNotSupportedException("不支持此 CPU 架构。")
    };

    public static IEnumerable<string> Arguments(JsonArray arguments, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var argument in arguments)
        {
            IEnumerable<string> values;
            if (argument is JsonValue text) values = [text.GetValue<string>()];
            else if (argument is JsonObject conditional && Allows(conditional["rules"]))
                values = conditional["value"] is JsonArray many
                    ? many.Select(value => value!.GetValue<string>())
                    : [conditional["value"]!.GetValue<string>()];
            else continue;

            foreach (var value in values)
                yield return Regex.Replace(value, @"\$\{([^}]+)\}", match =>
                    variables.TryGetValue(match.Groups[1].Value, out var replacement)
                        ? replacement
                        : throw new InvalidDataException($"此版本需要尚未支持的启动参数：{match.Value}"));
        }
    }

    public static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new InvalidDataException($"无效的相对路径：{relative}");
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"路径超出了游戏目录：{relative}");
        return full;
    }
}
