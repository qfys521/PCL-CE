using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Utils.OS;
using PCL.Portable;

[assembly: SupportedOSPlatform("linux")]

namespace PCL.Linux.Test;

[TestClass]
public sealed class LinuxLaunchTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("pcl test 启动 ").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void RulesApplyInOrderAndExcludeWindowsAndMissingFeatures()
    {
        Assert.IsTrue(LinuxRules.Allows(null));
        Assert.IsTrue(LinuxRules.Allows(JsonNode.Parse("""[{"action":"allow"},{"action":"disallow","os":{"name":"windows"}}]""")));
        Assert.IsFalse(LinuxRules.Allows(JsonNode.Parse("""[{"action":"allow","os":{"name":"windows"}}]""")));
        Assert.IsFalse(LinuxRules.Allows(JsonNode.Parse("""[{"action":"allow"},{"action":"disallow","os":{"name":"linux"}}]""")));
        Assert.IsFalse(LinuxRules.Allows(JsonNode.Parse("""[{"action":"allow","features":{"is_demo_user":true}}]""")));
        Assert.IsTrue(LinuxRules.Allows(JsonNode.Parse("""[{"action":"allow","features":{"is_demo_user":false}}]""")));
    }

    [TestMethod]
    public void ArgumentListPreservesSpacesAndFiltersConditionalArguments()
    {
        var args = JsonNode.Parse("""["-cp","${classpath}",{"rules":[{"action":"allow","os":{"name":"windows"}}],"value":"bad"},{"rules":[{"action":"allow","os":{"name":"linux"}}],"value":["--gameDir","${game_directory}"]}]""")!.AsArray();
        var actual = LinuxRules.Arguments(args, new Dictionary<string, string> { ["classpath"] = "/a path/a.jar:/中文/b.jar", ["game_directory"] = "/game path" }).ToArray();
        CollectionAssert.AreEqual(new[] { "-cp", "/a path/a.jar:/中文/b.jar", "--gameDir", "/game path" }, actual);
    }

    [TestMethod]
    public void RejectsTraversalAbsoluteAndWindowsPaths()
    {
        foreach (var path in new[] { "../escape", "/tmp/escape", "a/../../escape", "a\\escape" })
            Assert.Throws<InvalidDataException>(() => LinuxRules.ResolvePath(_directory, path));
    }

    [TestMethod]
    public void OfflineUuidMatchesMinecraftNameBasedUuid()
    {
        Assert.AreEqual("b50ad385829d3141a2167e7d7539ba7f", LaunchAccount.Offline("Notch").Uuid);
        Assert.Throws<ArgumentException>(() => LaunchAccount.Offline("a b"));
    }

    [TestMethod]
    public async Task DigestFailureKeepsExistingFileAndCleansPartialDownload()
    {
        var path = Path.Combine(_directory, "download.jar");
        await File.WriteAllTextAsync(path, "old");
        using var client = new HttpClient(new FixtureHandler(new Dictionary<string, byte[]> { ["https://test/file"] = Encoding.UTF8.GetBytes("corrupt") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedDownloads(client).FetchAsync("https://test/file", path, Hash(Encoding.UTF8.GetBytes("expected")), default));
        Assert.AreEqual("old", await File.ReadAllTextAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.part").Length);
    }

    [TestMethod]
    public async Task InstallAndPrepareSelectLinuxLibrariesVerifyAssetsAndPreservePaths()
    {
        const string id = "test version";
        var source = new Dictionary<string, byte[]>();
        JsonObject Descriptor(string url, byte[] content, string? path = null)
        {
            source[url] = content;
            var node = new JsonObject { ["url"] = url, ["sha1"] = Hash(content) };
            if (path is not null) node["path"] = path;
            return node;
        }
        byte[] NativeZip()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("libnative.so").Open())) writer.Write("native");
                using (var writer = new StreamWriter(zip.CreateEntry("META-INF/ignore.so").Open())) writer.Write("ignore");
                using (var writer = new StreamWriter(zip.CreateEntry("windows.dll").Open())) writer.Write("ignore");
            }
            return stream.ToArray();
        }
        var assetBytes = Encoding.UTF8.GetBytes("asset");
        var assetHash = Hash(assetBytes);
        source[$"https://resources.download.minecraft.net/{assetHash[..2]}/{assetHash}"] = assetBytes;
        var assetIndex = new JsonObject { ["objects"] = new JsonObject { ["a"] = new JsonObject { ["hash"] = assetHash } } };
        var index = Descriptor("https://test/assets", Encoding.UTF8.GetBytes(assetIndex.ToJsonString()));
        index["id"] = "test";
        var metadata = new JsonObject
        {
            ["mainClass"] = "net.minecraft.client.main.Main", ["type"] = "release",
            ["assetIndex"] = index, ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
            ["downloads"] = new JsonObject { ["client"] = Descriptor("https://test/game", Encoding.UTF8.GetBytes("game")) },
            ["arguments"] = JsonNode.Parse("""{"jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}"],"game":["--username","${auth_player_name}","--gameDir","${game_directory}","--accessToken","${auth_access_token}"]}"""),
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = Descriptor("https://test/library", Encoding.UTF8.GetBytes("library"), "group/lib.jar"),
                        ["classifiers"] = new JsonObject { ["natives-linux"] = Descriptor("https://test/native", NativeZip(), "group/native.jar") }
                    },
                    ["natives"] = new JsonObject { ["linux"] = "natives-linux", ["windows"] = "natives-windows" }
                },
                JsonNode.Parse("""{"rules":[{"action":"allow","os":{"name":"windows"}}],"downloads":{"artifact":{"path":"windows.jar","url":"https://must-not-download/windows"}}}""")
            },
            ["logging"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["argument"] = "-Dlog4j.configurationFile=${path}",
                    ["file"] = Descriptor("https://test/logging", Encoding.UTF8.GetBytes("logging"))
                }
            }
        };
        metadata["logging"]!["client"]!["file"]!["id"] = "test.xml";
        var metadataBytes = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        source["https://test/version"] = metadataBytes;
        var handler = new FixtureHandler(source);
        using var client = new HttpClient(handler);
        var launcher = new LinuxGameLauncher(_directory, client);
        await launcher.InstallAsync(new(id, "release", "https://test/version", Hash(metadataBytes)), null, default);
        var game = await launcher.PrepareAsync(id, new("/usr/bin/java", 2048, LaunchAccount.Offline("Player")), null, default);
        Assert.AreEqual(21, game.JavaMajorVersion);
        Assert.AreEqual(_directory, game.StartInfo.WorkingDirectory);
        Assert.IsTrue(game.StartInfo.ArgumentList.Contains(_directory));
        var classpath = game.StartInfo.ArgumentList[game.StartInfo.ArgumentList.IndexOf("-cp") + 1];
        Assert.IsTrue(classpath.Contains(Path.Combine(_directory, "libraries", "group", "lib.jar") + ":", StringComparison.Ordinal));
        Assert.IsFalse(classpath.Contains("native.jar", StringComparison.Ordinal));
        var nativeFolder = game.StartInfo.ArgumentList.Single(arg => arg.StartsWith("-Djava.library.path=", StringComparison.Ordinal)).Split('=', 2)[1];
        Assert.IsTrue(File.Exists(Path.Combine(nativeFolder, "libnative.so")));
        Assert.IsFalse(File.Exists(Path.Combine(nativeFolder, "windows.dll")));
        Assert.IsFalse(Directory.Exists(Path.Combine(nativeFolder, "META-INF")));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "assets", "objects", assetHash[..2], assetHash)));
        Assert.IsTrue(game.StartInfo.ArgumentList.Any(arg => arg.Contains("log_configs/test.xml", StringComparison.Ordinal)));
        Assert.AreEqual(source.Count, handler.Requests);
    }

    [TestMethod]
    public void NativeExtractionRejectsZipTraversal()
    {
        var archive = Path.Combine(_directory, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("../escape.so");
        Assert.Throws<InvalidDataException>(() => LinuxGameLauncher.ExtractNatives(archive, Path.Combine(_directory, "natives")));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "escape.so")));
    }

    [TestMethod]
    public void CoreSystemInfoDoesNotLoadKernel32OnLinux() => Assert.IsFalse(SystemInfo.IsGBKEncoding);

    [TestMethod]
    public void JavaParserReadsExecutableOutputAndUsesUnixJavaName()
    {
        var script = Path.Combine(_directory, "java");
        File.WriteAllText(script, "#!/bin/sh\nprintf '    java.version = 21.0.12\n    os.arch = amd64\n' >&2\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = new CommandJavaParser().Parse(script);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(21, result.Value.MajorVersion);
        Assert.AreEqual(script, result.Value.JavaExePath);
    }

    [TestMethod]
    public void SettingsKeepTokensPrivateAndRemoveThemOnSignOut()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        try
        {
            var settings = new LinuxSettings
            {
                MicrosoftAccount = new() { UserName = "Test", RefreshToken = "test-refresh-token" }
            };
            settings.Save();
            var path = Path.Combine(_directory, "pcl-ce", "linux.json");
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.AreEqual("test-refresh-token", LinuxSettings.Load().MicrosoftAccount!.RefreshToken);
            settings.MicrosoftAccount = null;
            settings.Save();
            Assert.IsFalse(File.ReadAllText(path).Contains("test-refresh-token", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private sealed class FixtureHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes))
                throw new InvalidOperationException($"Unexpected download: {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
