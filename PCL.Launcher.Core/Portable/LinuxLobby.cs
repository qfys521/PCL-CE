using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PCL.Core.Link;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Link.Scaffolding.Client.Requests;
using PCL.Core.Link.Scaffolding.Server;

namespace PCL.Portable;

/// <summary>Native EasyTier transport using the same room codes and Scaffolding protocol as Windows.</summary>
public sealed class LinuxLobby(string toolDirectory, string machineId, HttpClient client) : IAsyncDisposable
{
    public const string EasyTierVersion = "2.6.4";
    private const string PackageName = "easytier-linux-x86_64-v2.6.4";
    private const string PackageHash = "61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6";
    private const string HostPrefix = "scaffolding-mc-server-";
    private Process? _core;
    private Task? _output;
    private ScaffoldingServer? _server;
    private ScaffoldingClient? _scaffolding;
    private BroadcastLocal? _broadcast;
    private int _rpcPort;
    public string? RoomCode { get; private set; }
    public int? LocalGamePort { get; private set; }
    public event Action<string>? Log;
    public event Action<IReadOnlyList<PlayerProfile>>? PlayersChanged;
    public bool Active => _core is not null && !_core.HasExited;
    private string CorePath => Path.Combine(toolDirectory, PackageName, "easytier-core");
    private string CliPath => Path.Combine(toolDirectory, PackageName, "easytier-cli");

    public async Task EnsureToolsAsync(IProgress<string>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (File.Exists(CorePath) && File.Exists(CliPath)) return;
        Directory.CreateDirectory(toolDirectory);
        var archivePath = Path.Combine(toolDirectory, PackageName + ".zip");
        progress?.Report("正在下载 Linux EasyTier 联机组件…");
        await new VerifiedDownloads(client).FetchAsync($"https://github.com/EasyTier/EasyTier/releases/download/v{EasyTierVersion}/{PackageName}.zip", archivePath, PackageHash, token);
        using var archive = ZipFile.OpenRead(archivePath);
        var target = Path.Combine(toolDirectory, PackageName);
        Directory.CreateDirectory(target);
        foreach (var binary in new[] { "easytier-core", "easytier-cli" })
        {
            var entry = archive.Entries.Single(entry => entry.Name == binary);
            var path = Path.Combine(target, binary);
            entry.ExtractToFile(path, true);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task<string> CreateAsync(string username, int gamePort, string relays, IProgress<string>? progress, CancellationToken token)
    {
        if (gamePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(gamePort));
        if (Active) throw new InvalidOperationException("请先离开当前大厅。");
        await EnsureToolsAsync(progress, token);
        var context = ScaffoldingServerContext.Create(username, gamePort, machineId, "PCL CE Linux 0.2.0, EasyTier " + EasyTierVersion);
        var serverPort = NewPort();
        try
        {
            _server = new ScaffoldingServer(serverPort, context);
            _server.PlayerProfilePing += players => PlayersChanged?.Invoke(players);
            _server.Start();
            StartCore(context.UserLobbyInfo, HostPrefix + serverPort, true, gamePort, serverPort, relays);
            await WaitForRpcAsync(progress, token);
            RoomCode = context.UserLobbyInfo.FullCode;
            PlayersChanged?.Invoke(context.PlayerProfiles);
            return RoomCode;
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<int> JoinAsync(string username, string code, string relays, IProgress<string>? progress, CancellationToken token)
    {
        if (Active) throw new InvalidOperationException("请先离开当前大厅。");
        if (!LobbyCodeGenerator.TryParse(code.Trim(), out var room)) throw new InvalidDataException("大厅编号无效，请使用 U/ 开头的 Scaffolding 房间编号。");
        await EnsureToolsAsync(progress, token);
        try
        {
            StartCore(room, "pcl-linux-" + Guid.NewGuid().ToString("N")[..8], false, 0, 0, relays);
            await WaitForRpcAsync(progress, token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                var node = JsonNode.Parse((await CliAsync(["-o", "json", "node"], timeout.Token)).Output)!;
                if (!string.IsNullOrWhiteSpace(node["ipv4_addr"]?.ToString())) break;
                progress?.Report("正在分配大厅网络地址…");
                await Task.Delay(500, timeout.Token);
            }
            JsonObject? host = null;
            while (host is null)
            {
                progress?.Report("正在连接大厅房主…");
                var peers = JsonNode.Parse((await CliAsync(["-o", "json", "peer"], timeout.Token)).Output)!.AsArray();
                var hosts = peers.OfType<JsonObject>().Where(peer => peer["hostname"]?.ToString().StartsWith(HostPrefix, StringComparison.Ordinal) == true).ToArray();
                if (hosts.Length > 1) throw new InvalidDataException("大厅中存在多个房主。");
                host = hosts.FirstOrDefault();
                if (host is null) await Task.Delay(800, timeout.Token);
            }
            var hostIp = host["ipv4"]!.ToString().Split('/')[0];
            var port = int.Parse(host["hostname"]!.ToString()[HostPrefix.Length..]);
            var forwarded = await ForwardAsync(hostIp, port, timeout.Token);
            _scaffolding = new ScaffoldingClient("127.0.0.1", forwarded, username, machineId, "PCL CE Linux 0.2.0");
            _scaffolding.Heartbeat += (players, latency) => PlayersChanged?.Invoke(players);
            _scaffolding.ServerShuttedDown += () => Log?.Invoke("与大厅房主的连接已断开。");
            await _scaffolding.ConnectAsync(timeout.Token);
            var gamePort = await _scaffolding.SendRequestAsync(new GetServerPortRequest(), timeout.Token);
            LocalGamePort = await ForwardAsync(hostIp, gamePort, timeout.Token);
            RoomCode = code.Trim();
            _broadcast = new BroadcastLocal("PCL CE 联机大厅", LocalGamePort.Value);
            _broadcast.Start();
            var players = await _scaffolding.SendRequestAsync(new GetPlayerProfileListRequest(), timeout.Token);
            PlayersChanged?.Invoke(players);
            return LocalGamePort.Value;
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<ProcessResult> CliAsync(IEnumerable<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(CliPath) { WorkingDirectory = Path.GetDirectoryName(CliPath) };
        start.ArgumentList.Add("--rpc-portal"); start.ArgumentList.Add("127.0.0.1:" + _rpcPort);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var result = await PortableProcess.RunAsync(start, timeout.Token);
        if (result.ExitCode != 0) throw new IOException("EasyTier：" + result.Output);
        return result;
    }

    private void StartCore(LobbyInfo room, string hostname, bool host, int gamePort, int serverPort, string relays)
    {
        _rpcPort = NewPort();
        var start = new ProcessStartInfo(CorePath)
        {
            WorkingDirectory = Path.GetDirectoryName(CorePath), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
        Add("--no-tun", "--multi-thread", "--network-name", room.NetworkName, "--network-secret", room.NetworkSecret,
            "--hostname", hostname, "--rpc-portal", "127.0.0.1:" + _rpcPort, "--private-mode", "true", "--encryption-algorithm", "aes-gcm",
            "--bind-device", "false");
        if (host) Add("-i", "10.114.51.41", "--tcp-whitelist", serverPort.ToString(), "--tcp-whitelist", gamePort.ToString());
        else Add("-d", "--tcp-whitelist", "0");
        Add("-l", "tcp://0.0.0.0:0", "-l", "udp://0.0.0.0:0");
        foreach (var peer in relays.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(peer, UriKind.Absolute, out var uri) || uri.Scheme is not ("tcp" or "udp" or "ws" or "wss" or "quic"))
                throw new InvalidDataException($"无效的节点地址：{peer}");
            Add("-p", peer);
        }
        _core = Process.Start(start) ?? throw new IOException("无法启动 EasyTier。");
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
                Log?.Invoke(line.Replace(room.NetworkSecret, "[REDACTED]", StringComparison.Ordinal));
        }
        _output = Task.WhenAll(ReadAsync(_core.StandardOutput), ReadAsync(_core.StandardError));
    }

    private async Task WaitForRpcAsync(IProgress<string>? progress, CancellationToken token)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (_core is null || _core.HasExited) throw new IOException("EasyTier 已退出，请查看联机日志。");
            progress?.Report("正在启动联机网络…");
            try { await CliAsync(["-o", "json", "peer"], token); return; }
            catch (IOException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(500, token);
        }
        throw new TimeoutException("EasyTier RPC 启动超时。");
    }

    private async Task<int> ForwardAsync(string address, int port, CancellationToken token)
    {
        var local = NewPort();
        await CliAsync(["port-forward", "add", "tcp", "127.0.0.1:" + local, address + ":" + port], token);
        return local;
    }

    private static int NewPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    public async ValueTask DisposeAsync()
    {
        _broadcast?.Dispose(); _broadcast = null;
        if (_scaffolding is not null) { await _scaffolding.DisposeAsync(); _scaffolding = null; }
        if (_server is not null) { await _server.DisposeAsync(); _server = null; }
        if (_core is not null)
        {
            try { if (!_core.HasExited) _core.Kill(true); await _core.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
            _core.Dispose(); _core = null;
        }
        if (_output is not null) { await _output; _output = null; }
        RoomCode = null; LocalGamePort = null;
    }
}
