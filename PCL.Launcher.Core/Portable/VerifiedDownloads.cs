using System.Security.Cryptography;

namespace PCL.Portable;

/// <summary>Downloads to a temporary sibling and verifies the digest before replacing a cached file.</summary>
public sealed class VerifiedDownloads(HttpClient client)
{
    public async Task FetchAsync(string url, string destination, string? sha1, CancellationToken token)
    {
        if (await IsValidAsync(destination, sha1, token)) return;
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("只支持 HTTPS 下载。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(output, token);
            if (!await IsValidAsync(temporary, sha1, token))
                throw new InvalidDataException($"下载校验失败：{Path.GetFileName(destination)}");
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<bool> IsValidAsync(string path, string? sha1, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrEmpty(sha1)) return new FileInfo(path).Length > 0;
        await using var stream = File.OpenRead(path);
        var hash = sha1.Length switch
        {
            40 => await SHA1.HashDataAsync(stream, token),
            64 => await SHA256.HashDataAsync(stream, token),
            128 => await SHA512.HashDataAsync(stream, token),
            _ => throw new InvalidDataException("不支持的文件摘要格式。")
        };
        return string.Equals(Convert.ToHexString(hash), sha1, StringComparison.OrdinalIgnoreCase);
    }
}
