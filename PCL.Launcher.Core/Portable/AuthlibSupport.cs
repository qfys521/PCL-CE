using System.Text;
using System.Text.Json.Nodes;

namespace PCL.Portable;

/// <summary>Prepares the cross-platform authlib agent without Windows certificate-store flags.</summary>
public static class AuthlibSupport
{
    public static async Task ConfigureAsync(PreparedGame game, string server, string toolDirectory, HttpClient client, CancellationToken token)
    {
        var root = NormalizeServer(server);
        var metadata = await client.GetStringAsync(root, token);
        var descriptor = JsonNode.Parse(await client.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", token))!;
        var hash = descriptor["checksums"]?["sha256"]?.ToString() ?? throw new InvalidDataException("认证组件缺少 SHA-256。");
        var agent = Path.Combine(toolDirectory, "authlib-injector-" + hash + ".jar");
        await new VerifiedDownloads(client).FetchAsync(descriptor["download_url"]!.ToString(), agent, hash, token);
        game.StartInfo.ArgumentList.Insert(0, "-javaagent:" + agent + "=" + root);
        game.StartInfo.ArgumentList.Insert(1, "-Dauthlibinjector.side=client");
        game.StartInfo.ArgumentList.Insert(2, "-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata)));
    }

    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("认证服务器必须使用 HTTPS。");
        var root = uri.AbsoluteUri.TrimEnd('/');
        return root.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase) ? root[..^11] : root;
    }
}
