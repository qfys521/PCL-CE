using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL;
using PCL.Core.App.Region;
using PCL.Portable;

namespace PCL.Linux.Test;

/// <summary>Linux 兼容性回归：下载源镜像回退、大小写不敏感文件解析、ARM64 Java、区域限制放行。</summary>
[TestClass]
public sealed class CompatibilityTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init() => _root = Directory.CreateTempSubdirectory("pcl compat 中文 ").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));

    private sealed class RewriteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    [TestMethod]
    public void MirrorRewritesOnlyMojangDownloadHostsAndPreservesPathAndQuery()
    {
        Assert.AreEqual("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            DownloadPolicy.RewriteToMirror(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")).AbsoluteUri);
        Assert.AreEqual("https://bmclapi2.bangbang93.com/v1/packages/abc/1.20.json",
            DownloadPolicy.RewriteToMirror(new Uri("https://launchermeta.mojang.com/v1/packages/abc/1.20.json")).AbsoluteUri);
        Assert.AreEqual("https://bmclapi2.bangbang93.com/v1/objects/ab/client.jar",
            DownloadPolicy.RewriteToMirror(new Uri("https://launcher.mojang.com/v1/objects/ab/client.jar")).AbsoluteUri);
        Assert.AreEqual("https://bmclapi2.bangbang93.com/v1/objects/cd/asset",
            DownloadPolicy.RewriteToMirror(new Uri("https://piston-data.mojang.com/v1/objects/cd/asset")).AbsoluteUri);
        Assert.AreEqual("https://bmclapi2.bangbang93.com/maven/com/example/lib/1/lib-1.jar?token=abc",
            DownloadPolicy.RewriteToMirror(new Uri("https://libraries.minecraft.net/com/example/lib/1/lib-1.jar?token=abc")).AbsoluteUri);
        Assert.AreEqual("https://bmclapi2.bangbang93.com/assets/ab/abcdef0123",
            DownloadPolicy.RewriteToMirror(new Uri("https://resources.download.minecraft.net/ab/abcdef0123")).AbsoluteUri);

        // 认证、API、Modrinth、CurseForge、Adoptium 以及普通站点保持原样。
        foreach (var untouched in new[]
                 {
                     "https://api.minecraftservices.com/authentication/login_with_xbox",
                     "https://session.minecraft.net/game/checkserver.jsp",
                     "https://api.modrinth.com/v2/project/jei/version",
                     "https://api.curseforge.com/v1/mods/search",
                     "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64",
                     "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/file.zip"
                 })
        {
            var uri = new Uri(untouched);
            Assert.IsFalse(DownloadPolicy.IsMojangEndpoint(uri), untouched);
            Assert.AreEqual(untouched, DownloadPolicy.RewriteToMirror(uri).AbsoluteUri, untouched);
        }

        Assert.IsTrue(DownloadPolicy.IsMojangEndpoint(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")));
        Assert.IsTrue(DownloadPolicy.IsMojangEndpoint(new Uri("https://libraries.minecraft.net/com/example/lib.jar")));
        Assert.IsFalse(DownloadPolicy.IsMojangEndpoint(new Uri("https://api.minecraftservices.com/authentication")));
    }

    [TestMethod]
    public void DownloadSourceParsesSettingsValuesAndHonoursEnvironmentOverride()
    {
        var previous = Environment.GetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable);
        try
        {
            Assert.AreEqual(DownloadSource.Auto, DownloadPolicy.ParseSource(null));
            Assert.AreEqual(DownloadSource.Auto, DownloadPolicy.ParseSource("AUTO"));
            Assert.AreEqual(DownloadSource.Official, DownloadPolicy.ParseSource("official"));
            Assert.AreEqual(DownloadSource.Mirror, DownloadPolicy.ParseSource("mirror"));
            Assert.AreEqual(DownloadSource.Auto, DownloadPolicy.ParseSource("unknown"));

            Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, null);
            Assert.AreEqual("auto", DownloadPolicy.CurrentSource);
            Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, "mirror");
            Assert.AreEqual("mirror", DownloadPolicy.CurrentSource);
            Assert.AreEqual(DownloadSource.Mirror, DownloadPolicy.Current);
        }
        finally { Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, previous); }
    }

    [TestMethod]
    public async Task AutoModeFallsBackToMirrorWhenOfficialMojangHostFails()
    {
        var previous = Environment.GetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable);
        Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, null);
        try
        {
            var payload = Encoding.UTF8.GetBytes("manifest from mirror");
            var official = new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            var mirror = new Uri("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
            var handler = new RewriteHandler(request => request.RequestUri == official
                ? throw new HttpRequestException("DNS failure")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            using var client = new HttpClient(handler);
            var target = Path.Combine(_root, "manifest.json");
            await new VerifiedDownloads(client).FetchAsync(official.AbsoluteUri, target, Sha1(payload), default);
            Assert.AreEqual("manifest from mirror", await File.ReadAllTextAsync(target));
            CollectionAssert.AreEqual(new[] { official.AbsoluteUri, mirror.AbsoluteUri }, handler.Requests);
            Assert.AreEqual(0, Directory.GetFiles(_root, "*.part").Length);
        }
        finally { Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, previous); }
    }

    [TestMethod]
    public async Task MirrorModePrefersMirrorAndFetchTextAsyncAlsoFallsBack()
    {
        var previous = Environment.GetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable);
        Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, "mirror");
        try
        {
            var payload = Encoding.UTF8.GetBytes("text");
            var official = new Uri("https://launchermeta.mojang.com/v1/packages/abc/1.20.json");
            var mirror = new Uri("https://bmclapi2.bangbang93.com/v1/packages/abc/1.20.json");
            var handler = new RewriteHandler(request => request.RequestUri == mirror
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                : throw new HttpRequestException("official unavailable"));
            using var client = new HttpClient(handler);
            await new VerifiedDownloads(client).FetchAsync(official.AbsoluteUri, Path.Combine(_root, "1.20.json"), Sha1(payload), default);
            CollectionAssert.AreEqual(new[] { mirror.AbsoluteUri }, handler.Requests);

            handler.Requests.Clear();
            var text = await new VerifiedDownloads(client).FetchTextAsync(official.AbsoluteUri, default);
            Assert.AreEqual("text", text);
            CollectionAssert.AreEqual(new[] { mirror.AbsoluteUri }, handler.Requests);
        }
        finally { Environment.SetEnvironmentVariable(DownloadPolicy.SourceEnvironmentVariable, previous); }
    }

    [TestMethod]
    public async Task MirrorHashMismatchIsRejectedAndNeverTriedAgainstOfficial()
    {
        var payload = Encoding.UTF8.GetBytes("corrupt");
        var official = new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var handler = new RewriteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var client = new HttpClient(handler);
        var target = Path.Combine(_root, "manifest.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedDownloads(client)
            .FetchAsync(official.AbsoluteUri, target, Sha1(Encoding.UTF8.GetBytes("expected")), default));
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.part").Length);
    }

    [TestMethod]
    public async Task NotFoundOnOfficialDoesNotFallBackToMirror()
    {
        var official = new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var handler = new RewriteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedDownloads(client)
            .FetchAsync(official.AbsoluteUri, Path.Combine(_root, "missing.json"), null, default));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public void ExistingFileNameCanBeResolvedCaseInsensitively()
    {
        var directory = Path.Combine(_root, "mods");
        Directory.CreateDirectory(directory);
        var existing = ZipAt(Path.Combine(directory, "JEI.jar"), ("fabric.mod.json", """{"id":"jei","name":"JEI","version":"1"}"""));
        Assert.AreEqual(existing, LocalMods.ResolveExistingCaseInsensitive(directory, "jei.jar"));
        Assert.IsNull(LocalMods.ResolveExistingCaseInsensitive(directory, "absent.jar"));
        Assert.IsNull(LocalMods.ResolveExistingCaseInsensitive(Path.Combine(_root, "missing"), "jei.jar"));
    }

    [TestMethod]
    public void ImportingDifferentCaseReusesSingleFileAndToggleRemoveWork()
    {
        var instance = Path.Combine(_root, "instance");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(mods);
        ZipAt(Path.Combine(mods, "JEI.jar"), ("fabric.mod.json", """{"id":"jei","name":"JEI","version":"1"}"""));
        var source = ZipAt(Path.Combine(_root, "jei.jar"), ("fabric.mod.json", """{"id":"jei","name":"JEI","version":"2"}"""));

        LocalMods.Import(source, instance);
        var files = Directory.GetFiles(mods);
        Assert.AreEqual(1, files.Length);
        Assert.AreEqual("JEI.jar", Path.GetFileName(files[0]));

        var mod = LocalMods.List(instance).Single();
        Assert.IsTrue(mod.Enabled);
        Assert.AreEqual("2", mod.Version);
        LocalMods.Toggle(mod);
        mod = LocalMods.List(instance).Single();
        Assert.IsFalse(mod.Enabled);
        Assert.AreEqual("JEI.jar.disabled", Path.GetFileName(mod.Path));
        LocalMods.Toggle(mod);
        mod = LocalMods.List(instance).Single();
        Assert.IsTrue(mod.Enabled);
        LocalMods.Remove(mod);
        Assert.AreEqual(0, LocalMods.List(instance).Count);
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(mods, ".pcl-trash")).Length);
    }

    [TestMethod]
    public void ModsDifferingOnlyByCaseAreDeduplicatedPreferringEnabledEntry()
    {
        var instance = Path.Combine(_root, "dedupe");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(mods);
        ZipAt(Path.Combine(mods, "JEI.jar"), ("fabric.mod.json", """{"id":"jei","name":"JEI","version":"1"}"""));
        ZipAt(Path.Combine(mods, "jei.jar.disabled"), ("fabric.mod.json", """{"id":"jei","name":"JEI","version":"0"}"""));

        var list = LocalMods.List(instance);
        Assert.AreEqual(1, list.Count);
        Assert.IsTrue(list[0].Enabled);
        Assert.AreEqual("JEI.jar", Path.GetFileName(list[0].Path));
    }

    [TestMethod]
    public void JavaRuntimeInstallerBuildsArchitectureSpecificAdoptiumUrl()
    {
        Assert.AreEqual(
            "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jre&os=linux&vendor=eclipse",
            JavaRuntimeInstaller.BuildAdoptiumUrl(17, Architecture.X64));
        Assert.AreEqual(
            "https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture=aarch64&image_type=jre&os=linux&vendor=eclipse",
            JavaRuntimeInstaller.BuildAdoptiumUrl(21, Architecture.Arm64));
        Assert.AreEqual(
            "https://api.adoptium.net/v3/assets/latest/8/hotspot?architecture=" +
            (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64") +
            "&image_type=jre&os=linux&vendor=eclipse",
            JavaRuntimeInstaller.BuildAdoptiumUrl(8, RuntimeInformation.ProcessArchitecture));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => JavaRuntimeInstaller.BuildAdoptiumUrl(4, Architecture.X64));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => JavaRuntimeInstaller.BuildAdoptiumUrl(17, Architecture.Wasm));
    }

    [TestMethod]
    public void LinuxRegionPolicyAlwaysAllowsMinecraftDownloads()
    {
        Assert.IsFalse(OperatingSystem.IsWindows(), "此测试用于验证 Linux 行为");
        Assert.IsFalse(RegionPolicy.IsRestrictionSupported);
        var assessment = RegionPolicy.AssessCurrent();
        Assert.IsFalse(assessment.IsRestricted);
        Assert.AreNotEqual(RegionClassification.Mainland, assessment.Classification);

        // 便携核心没有注册任何限制策略时也必须全部放行（区域限制是 Windows 桌面宿主的行为）。
        Assert.IsFalse(RestrictedFeatures.IsRestricted);
        Assert.IsTrue(RestrictedFeatures.Check("minecraft-install"));
        Assert.IsTrue(RestrictedFeatures.Check("minecraft-client-download"));
        Assert.IsTrue(RestrictedFeatures.Check("minecraft-repair"));
        Assert.IsTrue(RestrictedFeatures.Check("online-modpack-install"));
        RestrictedFeatures.ThrowIfRestricted("minecraft-install");
    }

    private static string ZipAt(string path, params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open());
            writer.Write(file.Text);
        }
        return path;
    }
}
