using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Portable;

public enum CatalogSource { Modrinth, CurseForge }
public sealed record CatalogProject(string Id, string Title, string Description, CatalogSource Source, string Kind, string? WebsiteUrl = null)
{
    public override string ToString() => $"{Title}\n{Description}";
}
public sealed record ContentFile(string Name, string Url, string? Hash);
public sealed record ContentDependency(string? Project, string? Version);
public sealed record ContentVersion(string Id, string Name, CatalogSource Source, IReadOnlyList<ContentFile> Files, IReadOnlyList<ContentDependency> Dependencies)
{
    public override string ToString() => Name;
}

/// <summary>Public catalogs share a portable download model. CurseForge credentials are supplied by the host.</summary>
public sealed class ContentCatalog(HttpClient client, string? curseForgeKey = null)
{
    private const string Modrinth = "https://api.modrinth.com/v2/";
    private const string CurseForge = "https://api.curseforge.com/v1/";

    public async Task<IReadOnlyList<CatalogProject>> SearchAsync(CatalogSource source, string kind, string query,
        string? minecraft, string? loader, CancellationToken token)
    {
        if (source == CatalogSource.Modrinth)
        {
            var facets = new List<string[]> { new[] { "project_type:" + kind } };
            if (!string.IsNullOrEmpty(minecraft)) facets.Add(["versions:" + minecraft]);
            if (!string.IsNullOrEmpty(loader) && kind == "mod") facets.Add(["categories:" + loader]);
            var url = $"{Modrinth}search?query={Uri.EscapeDataString(query)}&limit=30&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
            var json = await JsonAsync(url, source, token);
            return json["hits"]!.AsArray().Select(node => new CatalogProject(node!["project_id"]!.ToString(),
                node["title"]!.ToString(), node["description"]?.ToString() ?? "", source, kind)).ToArray();
        }
        var classId = kind switch { "modpack" => 4471, "resourcepack" => 12, "shader" => 6552, _ => 6 };
        var address = $"{CurseForge}mods/search?gameId=432&classId={classId}&pageSize=30&searchFilter={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrEmpty(minecraft)) address += "&gameVersion=" + Uri.EscapeDataString(minecraft);
        var result = await JsonAsync(address, source, token);
        return result["data"]!.AsArray().Select(node => new CatalogProject(node!["id"]!.ToString(), node["name"]!.ToString(),
            node["summary"]?.ToString() ?? "", source, kind, node["links"]?["websiteUrl"]?.ToString())).ToArray();
    }

    public async Task<IReadOnlyList<ContentVersion>> VersionsAsync(CatalogProject project, string? minecraft, string? loader, CancellationToken token)
    {
        if (project.Source == CatalogSource.Modrinth)
        {
            var url = $"{Modrinth}project/{Uri.EscapeDataString(project.Id)}/version";
            var filters = new List<string>();
            if (!string.IsNullOrEmpty(minecraft)) filters.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraft })));
            if (!string.IsNullOrEmpty(loader) && project.Kind == "mod") filters.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
            if (filters.Count > 0) url += "?" + string.Join('&', filters);
            var json = await JsonAsync(url, project.Source, token);
            return json.AsArray().Select(node => ParseModrinth(node!)).ToArray();
        }
        var address = $"{CurseForge}mods/{project.Id}/files?pageSize=50";
        if (!string.IsNullOrEmpty(minecraft)) address += "&gameVersion=" + Uri.EscapeDataString(minecraft);
        var type = loader switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => 0 };
        if (type > 0 && project.Kind == "mod") address += "&modLoaderType=" + type;
        return (await JsonAsync(address, project.Source, token))["data"]!.AsArray().Select(node => ParseCurseForge(node!)).ToArray();
    }

    public async Task<ContentVersion> VersionAsync(CatalogSource source, string version, string? project, CancellationToken token)
    {
        if (source == CatalogSource.Modrinth) return ParseModrinth(await JsonAsync(Modrinth + "version/" + Uri.EscapeDataString(version), source, token));
        if (project is null) throw new ArgumentException("CurseForge 文件需要项目 ID。");
        return ParseCurseForge((await JsonAsync($"{CurseForge}mods/{project}/files/{version}", source, token))["data"]!);
    }

    public async Task InstallModAsync(ContentVersion version, string instance, string minecraft, string? loader,
        IProgress<string>? progress, CancellationToken token)
    {
        await InstallRecursiveAsync(version, instance, minecraft, loader, new HashSet<string>(), progress, token);
    }

    private async Task InstallRecursiveAsync(ContentVersion version, string instance, string minecraft, string? loader,
        HashSet<string> visiting, IProgress<string>? progress, CancellationToken token)
    {
        if (!visiting.Add(version.Source + ":" + version.Id)) return;
        if (version.Files.Count == 0) throw new InvalidOperationException("作者禁止了此文件的第三方下载，请在项目网站下载后导入。");
        if (visiting.Count > 200) throw new InvalidDataException("模组依赖数量超过限制。");
        foreach (var dependency in version.Dependencies)
        {
            ContentVersion required;
            if (dependency.Version is { } id) required = await VersionAsync(version.Source, id, dependency.Project, token);
            else if (dependency.Project is { } project)
                required = (await VersionsAsync(new(project, project, "", version.Source, "mod"), minecraft, loader, token)).FirstOrDefault()
                           ?? throw new InvalidDataException($"找不到兼容的必要依赖：{project}");
            else continue;
            await InstallRecursiveAsync(required, instance, minecraft, loader, visiting, progress, token);
        }
        foreach (var file in version.Files)
        {
            if (!file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Report($"正在下载模组：{file.Name}");
            await new VerifiedDownloads(client).FetchAsync(file.Url, LinuxRules.ResolvePath(Path.Combine(instance, "mods"), file.Name), file.Hash, token);
        }
    }

    private async Task<JsonNode> JsonAsync(string url, CatalogSource source, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (source == CatalogSource.CurseForge)
        {
            if (string.IsNullOrWhiteSpace(curseForgeKey)) throw new InvalidOperationException("请先在设置中填写 CurseForge API Key，或使用 Modrinth。");
            request.Headers.Add("x-api-key", curseForgeKey);
        }
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
    }

    private static ContentVersion ParseModrinth(JsonNode node)
    {
        var files = node["files"]!.AsArray();
        var primary = files.FirstOrDefault(file => file!["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
        return new(node["id"]!.ToString(), node["name"]?.ToString() ?? node["version_number"]!.ToString(), CatalogSource.Modrinth,
            primary is null ? [] : [new(primary["filename"]!.ToString(), primary["url"]!.ToString(), primary["hashes"]?["sha512"]?.ToString() ?? primary["hashes"]?["sha1"]?.ToString())],
            (node["dependencies"] as JsonArray ?? []).Where(dep => dep!["dependency_type"]?.ToString() == "required")
                .Select(dep => new ContentDependency(dep!["project_id"]?.ToString(), dep["version_id"]?.ToString())).ToArray());
    }

    private static ContentVersion ParseCurseForge(JsonNode node)
    {
        var hash = (node["hashes"] as JsonArray ?? []).FirstOrDefault(value => value!["algo"]?.GetValue<int>() == 1)?["value"]?.ToString();
        return new(node["id"]!.ToString(), node["displayName"]?.ToString() ?? node["fileName"]!.ToString(), CatalogSource.CurseForge,
            node["downloadUrl"] is null ? [] : [new(node["fileName"]!.ToString(), node["downloadUrl"]!.ToString(), hash)],
            (node["dependencies"] as JsonArray ?? []).Where(dep => dep!["relationType"]?.GetValue<int>() == 3)
                .Select(dep => new ContentDependency(dep!["modId"]!.ToString(), null)).ToArray());
    }
}
