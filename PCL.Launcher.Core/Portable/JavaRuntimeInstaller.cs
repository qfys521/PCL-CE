using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace PCL.Portable;

/// <summary>Downloads a verified Eclipse Temurin Linux runtime from the official Adoptium API.</summary>
public sealed class JavaRuntimeInstaller(string directory, HttpClient client)
{
    public IEnumerable<string> InstalledPaths() => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "java", SearchOption.AllDirectories).Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "bin") : [];

    public async Task<string> InstallAsync(int major, IProgress<string>? progress, CancellationToken token)
    {
        if (major is < 8 or > 99) throw new ArgumentOutOfRangeException(nameof(major));
        var address = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=linux&vendor=eclipse";
        progress?.Report($"正在获取 Java {major} Linux 运行时…");
        var versions = JsonNode.Parse(await client.GetStringAsync(address, token))!.AsArray();
        var package = versions.FirstOrDefault()?["binary"]?["package"] ?? throw new InvalidOperationException($"没有找到 Java {major} Linux x64 运行时。");
        var hash = package["checksum"]!.ToString();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Java 运行时缺少有效的 SHA-256。");
        var target = Path.Combine(directory, major + "-" + hash[..12]);
        if (Directory.Exists(target) && FindJava(target) is { } installed) return installed;
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, hash + ".tar.gz");
        var stage = Path.Combine(directory, ".extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report($"正在下载并校验 Java {major}…");
            await new VerifiedDownloads(client).FetchAsync(package["link"]!.ToString(), archive, hash, token);
            Directory.CreateDirectory(stage);
            progress?.Report($"正在解压 Java {major}…");
            await using (var input = File.OpenRead(archive))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, stage, false, token);
            var java = FindJava(stage) ?? throw new InvalidDataException("运行时包中没有 bin/java。");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(java, File.GetUnixFileMode(java) | UnixFileMode.UserExecute);
            var relative = Path.GetRelativePath(stage, java);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return Path.Combine(target, relative);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    private static string? FindJava(string path) => Directory.EnumerateFiles(path, "java", SearchOption.AllDirectories)
        .FirstOrDefault(file => Path.GetFileName(Path.GetDirectoryName(file)) == "bin");
}
