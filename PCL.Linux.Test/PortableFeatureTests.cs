using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Server;
using PCL.Core.Link.Scaffolding.Client;
using PCL.Core.Link.Scaffolding.Client.Requests;
using PCL.Portable;

namespace PCL.Linux.Test;

[TestClass]
public sealed class PortableFeatureTests
{
    private string _root = null!;
    [TestInitialize] public void Init() => _root = Directory.CreateTempSubdirectory("pcl features 中文 ").FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private void Profile(string id, string json)
    {
        var path = new VersionResolver(_root).MetadataPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json);
    }
    private void BaseProfile() => Profile("base", """{"id":"base","mainClass":"Main","minecraftArguments":"--username ${auth_player_name} --gameDir \"${game_directory}\"","downloads":{"client":{"url":"https://fixture/game"}},"libraries":[],"javaVersion":{"majorVersion":8}}""");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA512.HashData(data));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private string Zip(string name, params (string Name, string Text)[] files)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open()); writer.Write(file.Text); }
        return path;
    }
    private sealed class Handler(Dictionary<string, byte[]> source) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            if (!source.TryGetValue(url, out var value)) throw new InvalidOperationException("Unexpected request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(value) });
        }
    }

    [TestMethod]
    public async Task InheritanceReplacesMavenVersionsKeepsBaseJarAndConcatenatesArguments()
    {
        Profile("base", """{"id":"base","libraries":[{"name":"org.test:a:1"},{"name":"org.test:b:1"}],"arguments":{"jvm":["base"],"game":["game"]},"downloads":{"client":{"url":"https://fixture/game"}}}""");
        Profile("loader", """{"id":"loader","inheritsFrom":"base","libraries":[{"name":"org.test:a:2"}],"arguments":{"jvm":["loader"]}}""");
        Profile("instance", """{"id":"instance","inheritsFrom":"loader","pclGameDirectory":"instances/instance"}""");
        var resolver = new VersionResolver(_root);
        var result = await resolver.ResolveAsync("instance", default);
        Assert.AreEqual("base", result.JarId); Assert.AreEqual("base", result.MinecraftVersion);
        CollectionAssert.AreEqual(new[] { "org.test:a:2", "org.test:b:1" }, result.Metadata["libraries"]!.AsArray().Select(lib => lib!["name"]!.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "base", "loader" }, result.Metadata["arguments"]!["jvm"]!.AsArray().Select(arg => arg!.ToString()).ToArray());
        Assert.AreEqual("org.test:a:1", (await resolver.ResolveAsync("base", default)).Metadata["libraries"]![0]!["name"]!.ToString());
    }

    [TestMethod]
    public async Task CyclicInheritanceFailsWithoutRecursionOverflow()
    {
        Profile("a", """{"inheritsFrom":"b"}"""); Profile("b", """{"inheritsFrom":"a"}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => new VersionResolver(_root).ResolveAsync("a", default));
    }

    [TestMethod]
    public void ArgumentsAndMavenClassifiersPreserveTheirMeaning()
    {
        CollectionAssert.AreEqual(new[] { "-Dtest=a b", "", "--path", "/中文 path", "$(literal)" }, PortableArguments.Split("-Dtest=\"a b\" '' --path '/中文 path' '$(literal)'").ToArray());
        Assert.Throws<FormatException>(() => PortableArguments.Split("'unfinished"));
        Assert.AreEqual("net/minecraft/client/1.0/client-1.0-mappings.tsrg", PortableMaven.PathOf("net.minecraft:client:1.0:mappings@tsrg"));
        Assert.Throws<InvalidDataException>(() => PortableMaven.PathOf("group:../../bad:1"));
    }

    [TestMethod]
    public async Task LegacyLaunchUsesIsolatedDirectoryAndPackArguments()
    {
        BaseProfile(); Profile("pack", """{"inheritsFrom":"base","pclGameDirectory":"instances/pack","pclJvmArguments":"-Dpack=hello","pclGameArguments":"--width 800"}""");
        using var client = new HttpClient(new Handler(new() { ["https://fixture/game"] = Bytes("game") }));
        var game = await new LinuxGameLauncher(_root, client).PrepareAsync("pack", new("/bin/java", 1024, LaunchAccount.Offline("Test"), "-Dextra='a b'"), null, default);
        Assert.AreEqual(Path.Combine(_root, "instances", "pack"), game.StartInfo.WorkingDirectory);
        Assert.IsTrue(game.StartInfo.ArgumentList.Contains("-Dpack=hello")); Assert.IsTrue(game.StartInfo.ArgumentList.Contains("-Dextra=a b"));
        Assert.IsTrue(game.StartInfo.ArgumentList.Contains("800")); Assert.IsTrue(game.StartInfo.ArgumentList.Contains(game.StartInfo.WorkingDirectory));
        Assert.IsTrue(game.StartInfo.ArgumentList.Any(arg => arg.EndsWith("versions/base/base.jar", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task JavaRuntimeDownloadsVerifiedArchiveAndMakesJavaExecutable()
    {
        byte[] archive;
        using (var buffer = new MemoryStream())
        {
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
            using (var writer = new TarWriter(gzip, true))
            using (var data = new MemoryStream(Bytes("#!/bin/sh\nprintf '    java.version = 17.0.1\\n    os.arch = amd64\\n' >&2\n")))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "runtime/bin/java") { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            archive = buffer.ToArray();
        }
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        using var client = new HttpClient(new Handler(new()
        {
            ["https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jre&os=linux&vendor=eclipse"] = Bytes(new JsonArray(new JsonObject
            { ["binary"] = new JsonObject { ["package"] = new JsonObject { ["checksum"] = hash, ["link"] = "https://fixture/java" } } }).ToJsonString()),
            ["https://fixture/java"] = archive
        }));
        var installer = new JavaRuntimeInstaller(Path.Combine(_root,"java"),client);
        var java = await installer.InstallAsync(17,null,default);
        Assert.IsTrue((File.GetUnixFileMode(java) & UnixFileMode.UserExecute) != 0);
        var parsed = new PCL.Core.Minecraft.Java.Parser.CommandJavaParser().Parse(java);
        Assert.IsTrue(parsed.IsSuccess); Assert.AreEqual(17,parsed.Value.MajorVersion);
        Assert.AreEqual(java,(await installer.InstallAsync(17,null,default)));
        Assert.AreEqual(1,installer.InstalledPaths().Count());
    }

    [TestMethod]
    public async Task ModularLoaderIgnoresActualBaseJarWhenProfileIdDiffers()
    {
        Profile("base", """{"id":"base","mainClass":"Main","libraries":[],"arguments":{"jvm":["-cp","${classpath}"],"game":[]},"downloads":{"client":{"url":"https://fixture/game"}}}""");
        Profile("neoforge-test", """{"inheritsFrom":"base","arguments":{"jvm":["-DignoreList=client-extra,${version_name}.jar"]}}""");
        using var client = new HttpClient(new Handler(new() { ["https://fixture/game"] = Bytes("game") }));
        var game = await new LinuxGameLauncher(_root, client).PrepareAsync("neoforge-test", new("java",1024,LaunchAccount.Offline("Test")),null,default);
        Assert.IsTrue(game.StartInfo.ArgumentList.Contains("-DignoreList=client-extra,neoforge-test.jar,base.jar"));
    }

    [TestMethod]
    public void LegacyLibrariesIncludeMainArtifactAndLinuxNatives()
    {
        using var client = new HttpClient();
        var libraries = new LinuxGameLauncher(_root, client).Libraries(JsonNode.Parse("""{"libraries":[{"name":"org.lwjgl:lwjgl:2.9.0","natives":{"linux":"natives-linux"}}]}""")!.AsObject()).ToArray();
        Assert.AreEqual(2, libraries.Length); Assert.IsFalse(libraries[0].Native); Assert.IsTrue(libraries[1].Native);
    }

    [TestMethod]
    public async Task FabricInstallPublishesInheritedProfileAndDependencies()
    {
        BaseProfile();
        using var client = new HttpClient(new Handler(new()
        {
            ["https://fixture/game"] = Bytes("game"), ["https://fixture/repo/org/test/loader/1/loader-1.jar"] = Bytes("loader"),
            ["https://meta.fabricmc.net/v2/versions/loader/base/1/profile/json"] = Bytes("""{"id":"fabric-test","inheritsFrom":"base","mainClass":"Loader","libraries":[{"name":"org.test:loader:1","url":"https://fixture/repo/"}]}""")
        }));
        var launcher = new LinuxGameLauncher(_root, client);
        var id = await new LoaderInstaller(launcher, client).InstallAsync(LoaderKind.Fabric, "base", "1", "/bin/java", null, default);
        Assert.AreEqual("fabric-test", id); Assert.AreEqual("base", (await launcher.ResolveAsync(id, default)).JarId);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "libraries/org/test/loader/1/loader-1.jar")));
    }

    [TestMethod]
    public async Task ModpackOverridesWinAndExportPreservesConfigWithoutAccounts()
    {
        BaseProfile();
        var contents = Bytes("download");
        var manifest = new JsonObject
        {
            ["game"] = "minecraft", ["name"] = "Test Pack", ["dependencies"] = new JsonObject { ["minecraft"] = "base" },
            ["files"] = new JsonArray(new JsonObject { ["path"] = "config/test.txt", ["hashes"] = new JsonObject { ["sha512"] = Hash(contents) }, ["downloads"] = new JsonArray("https://fixture/config") })
        };
        var pack = Zip("test.mrpack", ("modrinth.index.json", manifest.ToJsonString()), ("overrides/config/test.txt", "override"), ("client-overrides/config/test.txt", "client"));
        using var client = new HttpClient(new Handler(new() { ["https://fixture/game"] = Bytes("game"), ["https://fixture/config"] = contents }));
        var installer = new ModpackInstaller(new(_root, client), client);
        await installer.ImportAsync(pack, "my pack", "/bin/java", null, default);
        var instance = Path.Combine(_root, "instances/my pack");
        Assert.AreEqual("client", File.ReadAllText(Path.Combine(instance, "config/test.txt")));
        File.WriteAllText(Path.Combine(instance, "launcher_accounts.json"), "secret");
        Directory.CreateDirectory(Path.Combine(instance, "logs")); File.WriteAllText(Path.Combine(instance, "logs/latest.log"), "private");
        var export = Path.Combine(_root, "export.mrpack"); await installer.ExportAsync("my pack", export, default);
        using var archive = ZipFile.OpenRead(export);
        Assert.IsNotNull(archive.GetEntry("overrides/config/test.txt"));
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Contains("accounts", StringComparison.Ordinal) || entry.FullName.Contains("latest.log", StringComparison.Ordinal)));
        Assert.AreEqual("base", ModpackInstaller.Inspect(export).Minecraft);
        await Assert.ThrowsAsync<IOException>(() => installer.ImportAsync(pack, "my pack", "/bin/java", null, default));
        Assert.AreEqual("client", File.ReadAllText(Path.Combine(instance, "config/test.txt")));
    }

    [TestMethod]
    public async Task BadPackCannotEscapeOrLeavePartialInstance()
    {
        BaseProfile();
        var pack = Zip("bad.mrpack", ("modrinth.index.json", """{"game":"minecraft","dependencies":{"minecraft":"base"},"files":[]}"""), ("overrides/../../escape", "bad"));
        using var client = new HttpClient(new Handler(new() { ["https://fixture/game"] = Bytes("game") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackInstaller(new(_root, client), client).ImportAsync(pack, "bad", "/bin/java", null, default));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "instances/bad")));
        Assert.IsFalse(File.Exists(new VersionResolver(_root).MetadataPath("bad")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escape")));
    }

    [TestMethod]
    public async Task BundledPackRenamesItsProfileChainAndPreservesJarAndSaves()
    {
        var pack = Zip("bundled.zip",
            (".minecraft/versions/base/base.json", """{"id":"base","mainClass":"Main","libraries":[],"minecraftArguments":"${auth_player_name}"}"""),
            (".minecraft/versions/base/base.jar", "local game"),
            (".minecraft/versions/modded/modded.json", """{"id":"modded","inheritsFrom":"base"}"""),
            (".minecraft/saves/test/level.dat", "world"));
        using var client = new HttpClient(new Handler(new()));
        var launcher = new LinuxGameLauncher(_root, client);
        await new ModpackInstaller(launcher, client).ImportAsync(pack, "Imported", "/bin/java", null, default);
        var resolved = await launcher.ResolveAsync("Imported", default);
        Assert.AreEqual("base", resolved.MinecraftVersion); Assert.AreEqual("Imported - base", resolved.JarId);
        Assert.AreEqual("local game", File.ReadAllText(new VersionResolver(_root).JarPath(resolved.JarId)));
        Assert.AreEqual("world", File.ReadAllText(Path.Combine(_root, "instances/Imported/saves/test/level.dat")));
    }

    [TestMethod]
    public async Task ForgeInstallerExecutesOnlyClientProcessorsAndVerifiesOutputs()
    {
        var tool = Zip("tool.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nMain-Class: example.Processor\n"));
        var toolBytes = File.ReadAllBytes(tool);
        var expected = Bytes("processed"); var output = Path.Combine(_root, "processed.txt");
        var profile = new JsonObject
        {
            ["libraries"] = new JsonArray(),
            ["data"] = new JsonObject { ["OUTPUT"] = new JsonObject { ["client"] = "{ROOT}/processed.txt" } },
            ["processors"] = new JsonArray(
                new JsonObject { ["sides"] = new JsonArray("server"), ["jar"] = "invalid:server:1" },
                new JsonObject { ["sides"] = new JsonArray("client"), ["jar"] = "example:tool:1", ["classpath"] = new JsonArray(),
                    ["args"] = new JsonArray("{OUTPUT}"), ["outputs"] = new JsonObject { ["{OUTPUT}"] = "'" + Hash(expected) + "'" } })
        };
        var installer = Zip("installer.jar", ("install_profile.json", profile.ToJsonString()), ("version.json", """{"id":"forge","inheritsFrom":"base"}"""));
        using (var archive = ZipFile.Open(installer, ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry("maven/example/tool/1/tool-1.jar").Open(); stream.Write(toolBytes);
        }
        var java = Path.Combine(_root, "java");
        File.WriteAllText(java, "#!/bin/sh\n[ \"$3\" = example.Processor ] || exit 7\nprintf processed > \"$4\"\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var client = new HttpClient(new Handler(new()));
        var result = await new LoaderInstaller(new(_root, client), client).InstallForgeArchiveAsync(installer, "base", java, null, default);
        Assert.AreEqual("forge", result["id"]!.ToString()); Assert.AreEqual("processed", File.ReadAllText(output));
        Assert.IsFalse(Directory.GetDirectories(Path.Combine(_root, ".pcl-installers"), "work-*").Any());
    }

    [TestMethod]
    public void LocalModMetadataToggleAndTrashWorkOnLinux()
    {
        var jar = Zip("example.jar", ("fabric.mod.json", """{"id":"example","name":"Example","version":"1.2"}"""));
        LocalMods.Import(jar, Path.Combine(_root, "instance"));
        var mod = LocalMods.List(Path.Combine(_root, "instance")).Single(); Assert.AreEqual("Example", mod.Name); Assert.AreEqual("1.2", mod.Version);
        LocalMods.Toggle(mod); mod = LocalMods.List(Path.Combine(_root, "instance")).Single(); Assert.IsFalse(mod.Enabled);
        LocalMods.Toggle(mod); mod = LocalMods.List(Path.Combine(_root, "instance")).Single(); LocalMods.Remove(mod);
        Assert.AreEqual(0, LocalMods.List(Path.Combine(_root, "instance")).Count);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_root, "instance/mods/.pcl-trash")).Length);
    }

    [TestMethod]
    public async Task CatalogInstallsPinnedRequiredDependenciesWithSha512()
    {
        var main = Bytes("main"); var dependency = Bytes("dependency");
        var handler = new Handler(new()
        {
            ["https://api.modrinth.com/v2/version/dep"] = Bytes(new JsonObject
            {
                ["id"] = "dep", ["name"] = "Dependency", ["files"] = new JsonArray(new JsonObject
                { ["filename"] = "dep.jar", ["url"] = "https://fixture/dep", ["hashes"] = new JsonObject { ["sha512"] = Hash(dependency) } }), ["dependencies"] = new JsonArray()
            }.ToJsonString()), ["https://fixture/dep"] = dependency, ["https://fixture/main"] = main
        });
        using var client = new HttpClient(handler);
        var version = new ContentVersion("main", "Main", CatalogSource.Modrinth, [new("main.jar", "https://fixture/main", Hash(main))], [new(null, "dep")]);
        await new ContentCatalog(client).InstallModAsync(version, _root, "base", "fabric", null, default);
        Assert.AreEqual("main", File.ReadAllText(Path.Combine(_root, "mods/main.jar")));
        Assert.AreEqual("dependency", File.ReadAllText(Path.Combine(_root, "mods/dep.jar")));
    }

    [TestMethod]
    public void CrashAnalysisReusesTheOriginalMemoryRuleOnLinux()
    {
        var report = "---- Minecraft Crash Report ----\njava.lang.OutOfMemoryError: Java heap space\n" + new string('a', 1000);
        var diagnosis = PortableCrashAnalysis.Analyze(report);
        Assert.IsTrue(diagnosis.Any(finding => finding.Cause == "OutOfMemory"));
    }

    [TestMethod]
    public async Task AuthlibAgentUsesVerifiedArtifactAndPortableJvmFlags()
    {
        var agent = Bytes("test agent"); var digest = Convert.ToHexString(SHA256.HashData(agent));
        using var client = new HttpClient(new Handler(new()
        {
            ["https://fixture/auth"] = Bytes("{\"meta\":{\"serverName\":\"Test\"}}"),
            ["https://authlib-injector.yushi.moe/artifact/latest.json"] = Bytes(new JsonObject
            { ["download_url"] = "https://fixture/agent", ["checksums"] = new JsonObject { ["sha256"] = digest } }.ToJsonString()),
            ["https://fixture/agent"] = agent
        }));
        var game = new PreparedGame(new System.Diagnostics.ProcessStartInfo("java"), "log", 21, "natives");
        await AuthlibSupport.ConfigureAsync(game, "https://fixture/auth/authserver", _root, client, default);
        Assert.IsTrue(game.StartInfo.ArgumentList[0].EndsWith("=https://fixture/auth", StringComparison.Ordinal));
        Assert.IsFalse(game.StartInfo.ArgumentList.Any(arg => arg.Contains("WINDOWS-ROOT", StringComparison.Ordinal)));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "authlib-injector-" + digest + ".jar")));
    }

    [TestMethod]
    public async Task ScaffoldingCanExchangePlayersAndPortsWithoutWindowsIdentity()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new ScaffoldingServer(port, ScaffoldingServerContext.Create("Host", 25565, "host-id", "Linux Test")); server.Start();
        await using var client = new ScaffoldingClient("127.0.0.1", port, "Guest", "guest-id", "Linux Test");
        await client.ConnectAsync(timeout.Token);
        Assert.AreEqual(25565, await client.SendRequestAsync(new GetServerPortRequest(), timeout.Token));
        var players = await client.SendRequestAsync(new GetPlayerProfileListRequest(), timeout.Token);
        CollectionAssert.AreEquivalent(new[] { "Host", "Guest" }, players.Select(player => player.Name).ToArray());
    }

    [TestMethod]
    public void GeneratedRoomsRoundTripWithSameNetworkAndSecret()
    {
        for (var i = 0; i < 100; i++)
        {
            var room = LobbyCodeGenerator.Generate();
            Assert.IsTrue(LobbyCodeGenerator.TryParse(room.FullCode, out var parsed));
            Assert.AreEqual(room.NetworkName, parsed.NetworkName);
            Assert.AreEqual(room.NetworkSecret, parsed.NetworkSecret);
        }
    }

    [TestMethod]
    public void HomepageUnderstandsOriginalControlsAndRejectsDtd()
    {
        var path = Path.Combine(_root, "Custom.xaml");
        File.WriteAllText(path, """<local:MyCard Title="Hello"><StackPanel><local:MyHint Text="Hint"/><local:MyButton Text="Open" EventType="打开网页" EventData="https://example.com"/></StackPanel></local:MyCard>""");
        var root = (StackPanel)HomepageRenderer.Load(path, (_, _) => { });
        Assert.AreEqual(1, root.Children.Count); Assert.IsInstanceOfType<Border>(root.Children[0]);
        File.WriteAllText(path, "<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><TextBlock>&e;</TextBlock>");
        Assert.Throws<System.Xml.XmlException>(() => HomepageRenderer.Load(path, (_, _) => { }));
    }
}
