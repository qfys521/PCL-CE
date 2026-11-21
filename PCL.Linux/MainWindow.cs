using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow : Window
{
    private readonly LinuxSettings _settings;
    private readonly HttpClient _client = LinuxHttpClientFactory.Create();
    private readonly TextBox _directory = new();
    private readonly TextBox _java = new();
    private readonly TextBox _name = new();
    private readonly TextBox _clientId = new() { Watermark = "Microsoft OAuth Client ID" };
    private readonly CheckBox _microsoft = new() { Content = "Microsoft 账号" };
    private readonly NumericUpDown _memory = new() { Minimum = 512, Maximum = 131072, Increment = 512, FormatString = "0" };
    private readonly ComboBox _installed = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择版本" };
    private readonly ComboBox _releases = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择 Minecraft 版本" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180 };
    private readonly TextBlock _status = new() { Text = "就绪", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _account = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancel = new() { Content = "取消", IsVisible = false };
    private readonly Grid _form = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _gameOperation;
    private bool _closeAfterGame;
    private readonly bool _smoke;

    public MainWindow(bool smoke)
    {
        _smoke = smoke;
        try { _settings = LinuxSettings.Load(); }
        catch (Exception ex) { _settings = new(); AppendLog($"读取设置失败：{ex.Message}"); }
        _directory.Text = _settings.GameDirectory;
        _java.Text = _settings.JavaPath;
        _name.Text = _settings.OfflineName;
        _memory.Value = _settings.MemoryMb;
        _clientId.Text = string.IsNullOrWhiteSpace(_settings.MicrosoftClientId)
            ? Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID") ?? "" : _settings.MicrosoftClientId;
        _microsoft.IsChecked = _settings.UseMicrosoftAccount;
        InitializeLayout();
        RefreshAccount();
        _installed.SelectionChanged += (_, _) => UpdateSelectedVersion();
        _cancel.Click += (_, _) => { if (_operation is not null) _operation.Cancel(); else _gameOperation?.Cancel(); };
        Closing += (_, e) =>
        {
            if (_operation is not null)
            {
                e.Cancel = true; _operation.Cancel();
                _status.Text = "正在取消操作 / 停止游戏…";
            }
            else if (_gameOperation is not null)
            {
                e.Cancel = true; _closeAfterGame = true; _gameOperation.Cancel();
                _status.Text = "正在停止游戏…";
            }
            else if (!smoke)
            {
                try { SaveSettings(); }
                catch (Exception ex) { e.Cancel = true; AppendLog($"保存设置失败：{ex.Message}"); }
            }
        };
        Closed += (_, _) =>
        {
            if (_lobby is { } lobby) Task.Run(async () => await lobby.DisposeAsync()).GetAwaiter().GetResult();
            _client.Dispose();
        };
        if (!smoke) RefreshInstalled();
    }

    private LinuxGameLauncher GameLauncher => new(_directory.Text ?? _settings.GameDirectory, _client);

    private Button Button(string label, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("pcl");
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private static Grid Row(string label, Control field, params Control[] actions)
    {
        var columns = "100,*" + string.Concat(actions.Select(_ => ",Auto"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(field, 1); row.Children.Add(field);
        for (var i = 0; i < actions.Length; i++) { Grid.SetColumn(actions[i], i + 2); row.Children.Add(actions[i]); }
        return row;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation; _form.IsEnabled = false; _cancel.IsEnabled = true; _cancel.IsVisible = true; _activity.IsVisible = true;
        try { await action(operation.Token); _status.Text = _gameOperation is not null ? "游戏正在运行" : "完成"; }
        catch (OperationCanceledException) { _status.Text = "已取消"; AppendLog("操作已取消。"); }
        catch (Exception ex) { _status.Text = "操作失败，详情见日志"; AppendLog(ex.Message); }
        finally
        {
            _operation = null; _form.IsEnabled = true; _activity.IsVisible = false;
            _cancel.IsEnabled = _gameOperation is not null; _cancel.IsVisible = _gameOperation is not null;
            _cancel.Content = _gameOperation is not null ? "停止游戏" : "取消";
        }
    }

    private void AppendLog(string message)
    {
        var text = _log.Text ?? "";
        if (text.Length > 50000) text = text[^30000..];
        _log.Text = text + $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        _log.CaretIndex = _log.Text.Length;
    }

    private IProgress<string> Progress => new Progress<string>(message => { _status.Text = message; });

    private void SaveSettings()
    {
        _settings.GameDirectory = Path.GetFullPath(_directory.Text ?? _settings.GameDirectory);
        _settings.JavaPath = _java.Text ?? _settings.JavaPath;
        _settings.MemoryMb = (int)(_memory.Value ?? 2048);
        _settings.OfflineName = _name.Text ?? "Player";
        _settings.MicrosoftClientId = _clientId.Text?.Trim() ?? "";
        _settings.UseMicrosoftAccount = _microsoft.IsChecked == true;
        _settings.SelectedVersion = _installed.SelectedItem as string;
        SaveExtendedSettings();
        _settings.Save();
    }

    private void RefreshInstalled()
    {
        var selected = _installed.SelectedItem as string ?? _settings.SelectedVersion;
        var versions = GameLauncher.GetInstalledVersions();
        _installed.ItemsSource = versions;
        _installed.SelectedItem = selected is not null && versions.Contains(selected) ? selected : versions.FirstOrDefault();
        UpdateSelectedVersion();
    }

    private async Task BrowseDirectoryAsync(CancellationToken token)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 Minecraft 游戏目录", AllowMultiple = false });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) { _directory.Text = path; RefreshInstalled(); }
    }

    private async Task BrowseJavaAsync(CancellationToken token)
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 Java 可执行文件（bin/java）", AllowMultiple = false });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) _java.Text = path;
    }

    private async Task DetectJavaAsync(CancellationToken token)
    {
        var paths = new List<string>();
        new LinuxJavaScanner().Scan(paths);
        if (!string.IsNullOrWhiteSpace(_java.Text)) paths.Insert(0, _java.Text);
        var installations = await Task.Run(() => paths.Distinct(StringComparer.Ordinal).Select(path => new CommandJavaParser().Parse(path))
            .Where(result => result.IsSuccess).Select(result => result.Value).DistinctBy(java => java.JavaFolder).ToArray(), token);
        foreach (var java in installations) AppendLog(java.ToDetailedString());
        if (installations.Length == 0) throw new InvalidOperationException("未找到 Java。请安装 OpenJDK 后选择 bin/java。");
        _java.Text = installations[0].JavaExePath;
    }

    private async Task RefreshReleasesAsync(CancellationToken token)
    {
        _status.Text = "正在获取 Mojang 版本列表…";
        var versions = _showAllVersions.IsChecked == true
            ? await GameLauncher.GetVersionsAsync(token) : await GameLauncher.GetReleasesAsync(token);
        _releases.ItemsSource = versions;
        _releases.SelectedIndex = 0;
    }

    private async Task InstallAsync(CancellationToken token)
    {
        if (_releases.SelectedItem is not GameRelease release) throw new InvalidOperationException("请先获取版本列表并选择版本。");
        SaveSettings();
        await GameLauncher.InstallAsync(release, Progress, token);
        RefreshInstalled(); _installed.SelectedItem = release.Id;
        AppendLog($"{release.Id} 下载与 SHA-1 校验完成。");
    }

    private MicrosoftProvider MicrosoftProvider => new(_clientId.Text?.Trim());

    private async Task LoginAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_clientId.Text))
            throw new InvalidOperationException("此源码构建不包含官方 Microsoft Client ID。请填写获准用于 Minecraft 登录的 OAuth 应用 Client ID，或设置 PCL_MS_CLIENT_ID 后重启。");
        var result = await MicrosoftProvider.AuthenticateAsync(new AuthenticationRequest
        {
            DeviceCodeHandler = async (context, ct) =>
            {
                var data = context.Data;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppendLog($"打开 {data.VerificationUri}，输入授权码：{data.UserCode}");
                    _status.Text = $"请在浏览器登录并输入：{data.UserCode}";
                    try { Process.Start(new ProcessStartInfo(data.VerificationUri!) { UseShellExecute = true }); }
                    catch { AppendLog("浏览器未能自动打开，请手动访问上方地址。"); }
                });
                var expires = DateTimeOffset.UtcNow.AddSeconds(data.ExpiresIn ?? 900);
                var interval = Math.Max(1, data.Interval ?? 5);
                while (DateTimeOffset.UtcNow < expires)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                    var authorization = await context.PollAsync(ct);
                    if (authorization is null) continue;
                    if (!authorization.IsError) return authorization;
                    if (authorization.Error == "slow_down") { interval += 5; continue; }
                    if (authorization.Error == "authorization_pending") continue;
                    throw new IdentityModelAuthenticationException(authorization.Error, authorization.ErrorDescription);
                }
                throw new TimeoutException("Microsoft 授权码已过期，请重新登录。");
            }
        }, token);
        SetAccount(result); _microsoft.IsChecked = true; SaveSettings();
        AppendLog($"已登录：{result.UserName}");
    }

    private void SetAccount(AuthenticationResult result)
    {
        _settings.MicrosoftAccount = new McProfile
        {
            UserName = result.UserName, Uuid = result.Uuid, AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken, ExpiresAt = result.ExpiresAt,
            ProfileType = ProfileType.Microsoft, TokenType = result.TokenType
        };
        RefreshAccount();
    }

    private void RefreshAccount() => _account.Text = _authlib.IsChecked == true
        ? _settings.AuthlibAccount is { } external ? $"第三方账号：{external.UserName}" : "请在账号设置中登录第三方账号"
        : _settings.MicrosoftAccount is { } account ? $"Microsoft 账号：{account.UserName}" : "尚未登录 Microsoft 账号";

    private async Task LaunchAsync(CancellationToken token)
    {
        if (_gameOperation is not null) throw new InvalidOperationException("游戏正在运行，请先停止当前游戏。");
        if (_installed.SelectedItem is not string id) throw new InvalidOperationException("请选择已安装的版本。");
        SaveSettings();
        var profileSettings = (await GameLauncher.ResolveAsync(id, token)).Metadata;
        var requiredJava = await GameLauncher.GetRequiredJavaVersionAsync(id, token);
        var javaPath = await SelectJavaAsync(requiredJava, token, profileSettings["pclJavaPath"]?.ToString());
        var java = await Task.Run(() => new CommandJavaParser().Parse(javaPath), token);
        if (!java.IsSuccess) throw java.Error;
        LaunchAccount account;
        if (_authlib.IsChecked == true)
        {
            var profile = _settings.AuthlibAccount ?? throw new InvalidOperationException("请在账号与服务中登录第三方账号。");
            var provider = new AuthlibProvider(AuthlibSupport.NormalizeServer(profile.Server ?? _settings.AuthlibServer));
            if (!await provider.ValidateAsync(profile, token))
            {
                _settings.AuthlibAccount = AuthlibProfile(await provider.RefreshAsync(profile, token));
                profile = _settings.AuthlibAccount; SaveSettings();
            }
            account = new(profile.UserName, profile.Uuid, profile.AccessToken, "mojang");
        }
        else if (_microsoft.IsChecked == true)
        {
            var profile = _settings.MicrosoftAccount ?? throw new InvalidOperationException("请先登录 Microsoft 账号。");
            if (profile.IsExpired)
            {
                SetAccount(await MicrosoftProvider.RefreshAsync(profile, token)); SaveSettings();
                profile = _settings.MicrosoftAccount!;
            }
            account = new(profile.UserName, profile.Uuid, profile.AccessToken);
        }
        else account = LaunchAccount.Offline(_name.Text ?? "");

        var memory = profileSettings["pclMemoryMb"]?.GetValue<int>() ?? 0;
        var prepared = await GameLauncher.PrepareAsync(id, new(java.Value.JavaExePath, memory > 0 ? memory : _settings.MemoryMb, account, _jvmArguments.Text ?? "", _gameArguments.Text ?? ""), Progress, token);
        try
        {
            if (_authlib.IsChecked == true)
                await AuthlibSupport.ConfigureAsync(prepared, _settings.AuthlibAccount!.Server ?? _settings.AuthlibServer,
                    Path.Combine(GameLauncher.GameDirectory, ".pcl-tools"), _client, token);
            token.ThrowIfCancellationRequested();
            _gameOperation = new CancellationTokenSource();
            _ = MonitorGameAsync(prepared, id, account, _gameOperation);
        }
        catch
        {
            Directory.Delete(prepared.NativesDirectory, true); throw;
        }
    }

    private async Task MonitorGameAsync(PreparedGame prepared, string id, LaunchAccount account, CancellationTokenSource game)
    {
        try { await RunGameAsync(prepared, id, account, game.Token); }
        catch (OperationCanceledException) { AppendLog("游戏已停止。"); }
        catch (Exception ex) { AppendLog(ex.Message); ShowDiagnosis(_log.Text ?? ""); _status.Text = "游戏退出，详情见运行日志"; }
        finally
        {
            try { Directory.Delete(prepared.NativesDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _gameOperation = null; game.Dispose();
            if (_operation is null) { _cancel.IsEnabled = false; _cancel.IsVisible = false; }
            if (_closeAfterGame) Close();
        }
    }

    private async Task RunGameAsync(PreparedGame prepared, string id, LaunchAccount account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(prepared.StartInfo) ?? throw new InvalidOperationException("无法启动 Java。");
        _cancel.Content = "停止游戏";
        _status.Text = $"{id} 正在运行";
        AppendLog($"游戏已启动（PID {process.Id}），日志：{prepared.LogPath}");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        using var writer = new StreamWriter(prepared.LogPath);
        using var writeLock = new SemaphoreSlim(1);
        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var safe = account.AccessToken.Length > 1 ? line.Replace(account.AccessToken, "[REDACTED]", StringComparison.Ordinal) : line;
                await writeLock.WaitAsync();
                try { await writer.WriteLineAsync(safe); }
                finally { writeLock.Release(); }
                Dispatcher.UIThread.Post(() => AppendLog(safe));
            }
        }
        await Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError), process.WaitForExitAsync());
        await writer.FlushAsync();
        AppendLog($"游戏已退出，退出码：{process.ExitCode}");
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Minecraft 异常退出（{process.ExitCode}），请查看 {prepared.LogPath}");
    }
}
