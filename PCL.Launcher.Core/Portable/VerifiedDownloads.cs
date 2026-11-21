using System.Net;
using System.Security.Cryptography;

namespace PCL.Portable;

/// <summary>
/// Downloads to a temporary sibling and verifies the digest before replacing a cached file.
/// 在 Linux 上按 <see cref="DownloadPolicy"/> 选择官方 / BMCLAPI 镜像端点：瞬时故障自动切换，
/// 但 4xx（如 404/403）与摘要校验失败绝不会回退到镜像（哈希校验始终保持严格）。
/// 临时 <c>.part</c> 文件与下载归档在成功和失败路径都会清理。
/// </summary>
public sealed class VerifiedDownloads(HttpClient client)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    public async Task FetchAsync(string url, string destination, string? sha1, CancellationToken token)
    {
        if (await IsValidAsync(destination, sha1, token)) return;
        var candidates = Candidates(url);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            Exception? failure = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var uri = candidates[Math.Min(attempt, candidates.Count - 1)];
                try
                {
                    await DownloadAsync(uri, temporary, sha1, token);
                    File.Move(temporary, destination, true);
                    return;
                }
                catch (Exception exception) when (attempt + 1 < MaxAttempts && IsTransient(exception, token))
                {
                    failure = exception;
                    TryDelete(temporary);
                    AppLog.Warn($"[Download] {Describe(uri)} 下载失败（第 {attempt + 1}/{MaxAttempts} 次）：{exception.Message}");
                    await Task.Delay(RetryDelay, token);
                }
            }
            throw failure ?? new IOException($"下载失败：{Path.GetFileName(destination)}");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>带进度提示的下载重载：用于在 UI 中显示当前文件名，不改变校验、镜像回退与临时文件语义。</summary>
    public async Task FetchAsync(string url, string destination, string? sha1, CancellationToken token, IProgress<string>? progress)
    {
        progress?.Report($"正在下载：{Path.GetFileName(destination)}");
        await FetchAsync(url, destination, sha1, token);
    }

    /// <summary>获取文本资源（版本清单、API 响应等），与 <see cref="FetchAsync"/> 使用相同的镜像回退策略。</summary>
    public async Task<string> FetchTextAsync(string url, CancellationToken token)
    {
        var candidates = Candidates(url);
        Exception? failure = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var uri = candidates[Math.Min(attempt, candidates.Count - 1)];
            try
            {
                using var response = await client.GetAsync(uri, token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
                return await response.Content.ReadAsStringAsync(token);
            }
            catch (Exception exception) when (attempt + 1 < MaxAttempts && IsTransient(exception, token))
            {
                failure = exception;
                AppLog.Warn($"[Download] {Describe(uri)} 获取失败（第 {attempt + 1}/{MaxAttempts} 次）：{exception.Message}");
                await Task.Delay(RetryDelay, token);
            }
        }
        throw failure ?? new IOException("获取下载内容失败。");
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

    private async Task DownloadAsync(Uri uri, string temporary, string? sha1, CancellationToken token)
    {
        TryDelete(temporary);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await source.CopyToAsync(output, token);
        if (!await IsValidAsync(temporary, sha1, token))
            throw new InvalidDataException($"下载校验失败：{Path.GetFileName(temporary)}");
    }

    /// <summary>
    /// 按下载源生成候选端点：<c>Auto</c> 官方 → 镜像；<c>Mirror</c> 镜像 → 官方；
    /// <c>Official</c> 或非 Mojang 端点只使用原始地址。
    /// </summary>
    private static List<Uri> Candidates(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var official) || official.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("只支持 HTTPS 下载。");
        var candidates = new List<Uri>(2);
        if (!DownloadPolicy.IsMojangEndpoint(official))
        {
            candidates.Add(official);
            return candidates;
        }
        var mirror = DownloadPolicy.RewriteToMirror(official);
        switch (DownloadPolicy.Current)
        {
            case DownloadSource.Mirror:
                candidates.Add(mirror);
                candidates.Add(official);
                break;
            case DownloadSource.Auto:
                candidates.Add(official);
                candidates.Add(mirror);
                break;
            default:
                candidates.Add(official);
                break;
        }
        return candidates;
    }

    /// <summary>仅超时、DNS/连接失败、连接重置与 5xx/408/429 视为可重试；4xx 与摘要校验失败不重试。</summary>
    private static bool IsTransient(Exception exception, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        return exception switch
        {
            InvalidDataException => false,
            OperationCanceledException => true,
            HttpRequestException http => http.StatusCode is null
                                         || (int)http.StatusCode >= 500
                                         || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
            IOException => true,
            _ => false
        };
    }

    /// <summary>日志地址：去掉查询参数与用户信息，避免把令牌写入日志。</summary>
    private static string Describe(Uri uri) => uri.IsAbsoluteUri ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}" : uri.ToString();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
