using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    private string SelectedId => _installed.SelectedItem as string ?? throw new InvalidOperationException("请先选择游戏版本。");
    private ContentCatalog Catalog => new(_client, _curseKey.Text);
    private ModpackInstaller Packs => new(GameLauncher, _client, _curseKey.Text);
    private JavaRuntimeInstaller JavaRuntimes => new(LinuxPaths.JavaRuntimeHome, _client);

    private static McProfile AuthlibProfile(AuthenticationResult result) => new()
    {
        UserName = result.UserName, Uuid = result.Uuid, AccessToken = result.AccessToken,
        ClientToken = result.ClientToken, Server = result.Server, ServerName = result.ServerName,
        LoginName = result.LoginName, ProfileType = ProfileType.Authlib, Provider = "authlib"
    };

    private async Task LoginAuthlibAsync(CancellationToken token)
    {
        var server = AuthlibSupport.NormalizeServer(_authlibServer.Text ?? "");
        try
        {
            var result = await new AuthlibProvider(server).AuthenticateAsync(new AuthenticationRequest
            {
                Username = _authlibUsername.Text, Password = _authlibPassword.Text,
                ProfileSelector = async (candidates, ct) => await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var dialog = new Window { Title = "选择角色", Width = 380, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    var list = new ListBox { ItemsSource = candidates, SelectedIndex = 0, DisplayMemberBinding = new Avalonia.Data.Binding("Name") };
                    var panel = new DockPanel { Margin = new Avalonia.Thickness(20) };
                    var select = new Button { Content = "使用此角色", HorizontalAlignment = HorizontalAlignment.Stretch };
                    DockPanel.SetDock(select, Dock.Bottom); panel.Children.Add(select); panel.Children.Add(list); dialog.Content = panel;
                    select.Click += (_, _) => dialog.Close(list.SelectedItem as AuthenticationCandidate);
                    using var cancel = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
                    PrepareDialog(dialog);
                    return await dialog.ShowDialog<AuthenticationCandidate?>(this);
                })
            }, token);
            _settings.AuthlibAccount = AuthlibProfile(result); _authlib.IsChecked = true;
            RefreshAccountHeader(); SaveSettings(); AppendLog("已登录第三方账号：" + result.UserName);
        }
        finally { _authlibPassword.Text = ""; }
    }

    private async Task<string> SelectJavaAsync(int major, CancellationToken token, string? preferred = null)
    {
        var current = _java.Text ?? "";
        var paths = new List<string> { preferred ?? "", current };
        paths.AddRange(JavaRuntimes.InstalledPaths());
        new LinuxJavaScanner().Scan(paths);
        var found = await Task.Run(() => paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct()
            .Select(path => new CommandJavaParser().Parse(path)).Where(result => result.IsSuccess)
            .Select(result => result.Value).FirstOrDefault(java => java.MajorVersion == major), token);
        if (found is null)
        {
            var downloaded = await JavaRuntimes.InstallAsync(major, Progress, token);
            var result = await Task.Run(() => new CommandJavaParser().Parse(downloaded), token);
            if (!result.IsSuccess) throw result.Error;
            if (result.Value.MajorVersion != major) throw new InvalidDataException("下载的 Java 版本与要求不符。");
            found = result.Value;
            AppendLog($"已安装 Eclipse Temurin Java {major}。");
        }
        if (string.IsNullOrWhiteSpace(preferred)) _java.Text = found.JavaExePath;
        return found.JavaExePath;
    }

    private async Task DownloadJavaAsync(CancellationToken token)
    {
        var major = await GameLauncher.GetRequiredJavaVersionAsync(SelectedId, token);
        _java.Text = await JavaRuntimes.InstallAsync(major, Progress, token); SaveSettings();
    }

    private async Task LoadVersionSettingsAsync(CancellationToken token)
    {
        var profile = (await GameLauncher.ResolveAsync(SelectedId, token)).Metadata;
        _versionConfigLabel.Text = "当前版本：" + SelectedId;
        _versionJava.Text = profile["pclJavaPath"]?.ToString() ?? "";
        _versionMemory.Value = profile["pclMemoryMb"]?.GetValue<int>() ?? 0;
        _versionJvm.Text = profile["pclJvmArguments"]?.ToString() ?? "";
        _versionGame.Text = profile["pclGameArguments"]?.ToString() ?? "";
    }
    private async Task SaveVersionSettingsAsync(CancellationToken token)
    {
        var path = new VersionResolver(GameLauncher.GameDirectory).MetadataPath(SelectedId);
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        _ = PortableArguments.Split(_versionJvm.Text ?? ""); _ = PortableArguments.Split(_versionGame.Text ?? "");
        var memory = (int)(_versionMemory.Value ?? 0);
        if (memory is > 0 and < 512) throw new ArgumentException("内存至少为 512 MiB，或填 0 使用全局设置。");
        profile["pclJavaPath"] = _versionJava.Text ?? ""; profile["pclMemoryMb"] = memory;
        profile["pclJvmArguments"] = _versionJvm.Text ?? ""; profile["pclGameArguments"] = _versionGame.Text ?? "";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try { await File.WriteAllTextAsync(temporary, profile.ToJsonString(), token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<(string Minecraft, string? Loader)> CompatibilityAsync(CancellationToken token)
    {
        var resolved = await GameLauncher.ResolveAsync(SelectedId, token);
        var names = (resolved.Metadata["libraries"] as JsonArray ?? []).Select(lib => lib?["name"]?.ToString() ?? "").ToArray();
        var loader = names.Any(name => name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.Ordinal)) ? "fabric"
            : names.Any(name => name.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)) ? "quilt"
            : names.Any(name => name.StartsWith("net.neoforged:", StringComparison.Ordinal)) ? "neoforge"
            : names.Any(name => name.StartsWith("net.minecraftforge:", StringComparison.Ordinal)) ? "forge" : null;
        return (resolved.MinecraftVersion, loader);
    }

    private async Task RefreshLoaderVersionsAsync(CancellationToken token)
    {
        if (_releases.SelectedItem is not GameRelease release) throw new InvalidOperationException("请先获取并选择 Minecraft 版本。");
        var kind = (LoaderKind)(_loaderKind.SelectedItem ?? LoaderKind.Vanilla);
        _loaderVersions.ItemsSource = kind == LoaderKind.Vanilla ? Array.Empty<string>()
            : await new LoaderInstaller(GameLauncher, _client).GetVersionsAsync(kind, release.Id, token);
        _loaderVersions.SelectedIndex = 0;
    }

    private async Task InstallSelectedAsync(CancellationToken token)
    {
        if (_releases.SelectedItem is not GameRelease release) throw new InvalidOperationException("请先获取并选择 Minecraft 版本。");
        var kind = (LoaderKind)(_loaderKind.SelectedItem ?? LoaderKind.Vanilla);
        if (kind != LoaderKind.Vanilla && _loaderVersions.SelectedItem is not string) throw new InvalidOperationException("请获取并选择加载器版本。");
        SaveSettings();
        await GameLauncher.InstallAsync(release, Progress, token);
        var java = kind is LoaderKind.Forge or LoaderKind.NeoForge
            ? await SelectJavaAsync(await GameLauncher.GetRequiredJavaVersionAsync(release.Id, token), token) : _java.Text ?? "java";
        var id = await new LoaderInstaller(GameLauncher, _client).InstallAsync(kind, release.Id, _loaderVersions.SelectedItem as string, java, Progress, token);
        RefreshInstalled(); _installed.SelectedItem = id; SaveSettings();
        AppendLog($"{id} 安装完成。");
    }

    private async Task OpenInstanceAsync(CancellationToken token) => OpenLocal(await GameLauncher.GetInstanceDirectoryAsync(SelectedId, token));
    private async Task RepairInstanceAsync(CancellationToken token)
    {
        await GameLauncher.CompleteFilesAsync(SelectedId, Progress, token);
        AppendLog("游戏文件补全与校验完成。");
    }
    private async Task CreateInstanceAsync(CancellationToken token)
    {
        var id = _newInstance.Text?.Trim() ?? "";
        await GameLauncher.CreateInstanceAsync(SelectedId, id, token);
        RefreshInstalled(); _installed.SelectedItem = id; SaveSettings();
    }

    private async Task RefreshModsAsync(CancellationToken token)
    {
        var directory = await GameLauncher.GetInstanceDirectoryAsync(SelectedId, token);
        var mods = await Task.Run(() => LocalMods.List(directory), token);
        _mods.ItemsSource = mods;
        _modDetails.Text = $"{SelectedId} · 共 {mods.Count} 个模组，{mods.Count(mod => mod.Enabled)} 个启用";
    }
    private async Task ImportModsAsync(CancellationToken token)
    {
        var directory = await GameLauncher.GetInstanceDirectoryAsync(SelectedId, token);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入模组", AllowMultiple = true, FileTypeFilter = [new("Java 模组") { Patterns = ["*.jar"] }]
        });
        foreach (var file in files) { token.ThrowIfCancellationRequested(); if (file.TryGetLocalPath() is { } path) LocalMods.Import(path, directory); }
        await RefreshModsAsync(token);
    }
    private async Task ToggleModsAsync(CancellationToken token)
    {
        foreach (var mod in _mods.SelectedItems?.OfType<LocalMod>().ToArray() ?? []) LocalMods.Toggle(mod);
        await RefreshModsAsync(token);
    }
    private async Task RemoveModsAsync(CancellationToken token)
    {
        foreach (var mod in _mods.SelectedItems?.OfType<LocalMod>().ToArray() ?? []) LocalMods.Remove(mod);
        await RefreshModsAsync(token);
    }

    private async Task SearchContentAsync(CancellationToken token)
    {
        var compatibility = _installed.SelectedItem is string ? await CompatibilityAsync(token) : default;
        _projects.ItemsSource = await Catalog.SearchAsync((CatalogSource)(_catalogSource.SelectedItem ?? CatalogSource.Modrinth),
            _contentKind.SelectedItem as string ?? "mod", _search.Text ?? "", compatibility.Minecraft, compatibility.Loader, token);
        _projects.SelectedIndex = 0;
        await LoadContentVersionsAsync(token);
    }
    private async Task LoadContentVersionsAsync(CancellationToken token)
    {
        _contentVersions.ItemsSource = null;
        if (_projects.SelectedItem is not CatalogProject project) return;
        var compatibility = _installed.SelectedItem is string ? await CompatibilityAsync(token) : default;
        _contentVersions.ItemsSource = await Catalog.VersionsAsync(project, compatibility.Minecraft, compatibility.Loader, token);
        _contentVersions.SelectedIndex = 0;
    }
    private async Task InstallContentAsync(CancellationToken token)
    {
        if (_projects.SelectedItem is not CatalogProject project || _contentVersions.SelectedItem is not ContentVersion version)
            throw new InvalidOperationException("请先搜索资源并选择版本。");
        if (version.Files.Count == 0) throw new InvalidOperationException("作者禁止了此文件的第三方下载，请点击“项目网站”手动下载后导入。");
        if (project.Kind == "modpack")
        {
            var file = version.Files.FirstOrDefault() ?? throw new InvalidDataException("资源没有可下载文件。");
            var cache = LinuxRules.ResolvePath(LinuxPaths.DownloadsCache, file.Name);
            await new VerifiedDownloads(_client).FetchAsync(file.Url, cache, file.Hash, token);
            await ImportPackPathAsync(cache, token);
        }
        else
        {
            var directory = await GameLauncher.GetInstanceDirectoryAsync(SelectedId, token);
            if (project.Kind == "mod")
            {
                var compatibility = await CompatibilityAsync(token);
                await Catalog.InstallModAsync(version, directory, compatibility.Minecraft, compatibility.Loader, Progress, token);
            }
            else
            {
                // ext4/btrfs 区分大小写：远端文件名大小写与本地不一致时复用已有文件，避免重复下载。
                var contentDirectory = Path.Combine(directory, project.Kind == "shader" ? "shaderpacks" : "resourcepacks");
                foreach (var file in version.Files)
                {
                    var target = LocalMods.ResolveContentTarget(contentDirectory, file.Name);
                    await new VerifiedDownloads(_client).FetchAsync(file.Url, LinuxRules.ResolvePath(Path.GetDirectoryName(target)!, Path.GetFileName(target)), file.Hash, token);
                }
            }
        }
        AppendLog($"已安装：{project.Title} / {version.Name}");
        Notify("资源安装完成", project.Title + " / " + version.Name);
    }

    private async Task ImportPackAsync(CancellationToken token)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择整合包", FileTypeFilter = [new("Minecraft 整合包") { Patterns = ["*.zip", "*.mrpack"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await ImportPackPathAsync(path, token);
    }
    private async Task ImportPackPathAsync(string path, CancellationToken token)
    {
        var plan = ModpackInstaller.Inspect(path);
        var id = _packName.Text?.Trim();
        if (string.IsNullOrEmpty(id)) id = string.Concat(plan.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':' ? '_' : character));
        VersionResolver.ValidateId(id);
        // Read the base metadata first so installers use the Java required by this pack.
        if (plan.Format != "bundled") await GameLauncher.InstallMinecraftAsync(plan.Minecraft, Progress, token);
        var java = plan.Loader is LoaderKind.Forge or LoaderKind.NeoForge
            ? await SelectJavaAsync(await GameLauncher.GetRequiredJavaVersionAsync(plan.Minecraft, token), token) : _java.Text ?? "java";
        await Packs.ImportAsync(path, id, java, Progress, token);
        RefreshInstalled(); _installed.SelectedItem = id; SaveSettings();
        AppendLog($"已导入整合包：{plan.Name} → {id}");
        Notify("整合包导入完成", $"{plan.Name} → {id}");
    }
    private async Task ExportPackAsync(CancellationToken token)
    {
        var id = SelectedId;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出整合包", SuggestedFileName = id + ".mrpack", DefaultExtension = "mrpack",
            FileTypeChoices = [new("Modrinth 整合包") { Patterns = ["*.mrpack"] }]
        });
        if (file?.TryGetLocalPath() is { } path) { await Packs.ExportAsync(id, path, token); AppendLog("已导出：" + path); }
    }

    private async Task BrowseBackgroundAsync(CancellationToken token)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择背景图片", FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _background.Text = path;
    }
    private async Task BrowseHomepageAsync(CancellationToken token)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择自定义主页", FileTypeFilter = [new("PCL 主页") { Patterns = ["*.xaml", "*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _homepage.Text = path;
    }
    private void RefreshHomepage()
    {
        if (_pages.TryGetValue("launch", out var page)) _pages["launch"] = (page.Left, LaunchRight());
        if (_currentPage == "launch") Navigate("launch");
    }

    private LinuxLobby Lobby
    {
        get
        {
            if (_lobby is not null) return _lobby;
            _lobby = new LinuxLobby(LinuxPaths.ToolsHome, _settings.MachineId, _client);
            _lobby.Log += message => Dispatcher.UIThread.Post(() => AppendLog(message));
            _lobby.PlayersChanged += players => Dispatcher.UIThread.Post(() => _roomPlayers.Text = string.Join('\n', players.Select(player => player.Name + " · " + player.Vendor)));
            return _lobby;
        }
    }
    private async Task CreateLobbyAsync(CancellationToken token)
    {
        var code = await Lobby.CreateAsync(_name.Text ?? "Player", (int)(_roomPort.Value ?? 25565), _relays.Text ?? "", Progress, token);
        _roomCode.Text = code; _roomStatus.Text = "大厅已创建。请保持 Minecraft 的局域网世界开放，并分享大厅编号。";
        Notify("联机大厅已创建", "大厅编号：" + code);
    }
    private async Task JoinLobbyAsync(CancellationToken token)
    {
        var port = await Lobby.JoinAsync(_name.Text ?? "Player", _roomCode.Text ?? "", _relays.Text ?? "", Progress, token);
        _roomStatus.Text = $"已加入大厅。Minecraft 多人游戏可自动发现，或直接连接 127.0.0.1:{port}";
        Notify("已加入联机大厅", $"可在游戏内连接 127.0.0.1:{port}");
    }
    private async Task LeaveLobbyAsync(CancellationToken token)
    {
        if (_lobby is not null) await _lobby.DisposeAsync();
        _roomStatus.Text = "已离开大厅"; _roomPlayers.Text = "";
    }
    private async Task CopyRoomAsync(CancellationToken token)
    {
        if (Clipboard is { } clipboard && _lobby?.RoomCode is { } code) await clipboard.SetTextAsync(code);
        else throw new InvalidOperationException("请先创建或加入大厅。");
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("网页地址必须使用 http 或 https。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private static void OpenLocal(string path)
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path)); Process.Start(start);
    }

    private async Task AnalyzeLogAsync(CancellationToken token)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "选择游戏日志或崩溃报告", FileTypeFilter = [new("文本日志") { Patterns = ["*.log", "*.txt"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("请选择小于 8 MiB 的日志文件。");
        ShowDiagnosis(await File.ReadAllTextAsync(path, token));
    }
    private void ShowDiagnosis(string text)
    {
        var results = PortableCrashAnalysis.Analyze(text);
        AppendLog(results.Count == 0 ? "未检测到明确的崩溃原因，请检查完整游戏日志。"
            : string.Join("\n\n", results.Select(result => result.Cause + "：" + result.Advice + "\n" + string.Join('\n', result.Evidence))));
    }

    private async void HandleHomepageAction(string type, string data)
    {
        await RunAsync(async token =>
        {
            var args = data.Split('|');
            switch (type)
            {
                case "打开网页": case "OpenUrl": OpenUrl(data); break;
                case "打开文件": case "OpenFile": OpenLocal(data); break;
                case "执行命令": case "ExecuteCommand":
                    if (!await ConfirmActionAsync("自定义主页请求执行程序", data)) break;
                    var command = new ProcessStartInfo(args[0]) { UseShellExecute = false };
                    foreach (var argument in PortableArguments.Split(args.ElementAtOrDefault(1) ?? "")) command.ArgumentList.Add(argument);
                    var result = await PortableProcess.RunAsync(command, token); AppendLog(result.Output);
                    if (result.ExitCode != 0) throw new IOException("程序退出码：" + result.ExitCode);
                    break;
                case "复制文本": case "CopyText": if (Clipboard is { } clipboard) await clipboard.SetTextAsync(data); break;
                case "启动游戏": case "LaunchGame":
                    if (!string.IsNullOrWhiteSpace(args[0]) && args[0] != "\\current") _installed.SelectedItem = args[0];
                    await LaunchAsync(token); break;
                case "刷新主页": case "RefreshHomepage": case "刷新页面": case "RefreshPage": RefreshHomepage(); break;
                case "切换页面": case "SwitchPage":
                    var page = args[0] switch
                    {
                        "启动" or "Launch" or "0" => "launch", "下载" or "Download" or "1" => "download", "设置" or "Setup" or "2" => "settings",
                        "更多" or "Other" or "3" => "more", "联机" => "lobby", _ => args[0]
                    };
                    Navigate(page); break;
                case "导入整合包": case "ImportModpack": case "安装整合包": case "InstallModpack":
                    if (string.IsNullOrEmpty(data)) await ImportPackAsync(token); else await ImportPackPathAsync(data, token); break;
                case "弹出提示": case "ShowHint": AppendLog(args[0].Replace("\\n", "\n")); break;
                case "弹出窗口": case "ShowDialog":
                    var dialog = new Window { Title = args.Length > 1 ? args[0] : "PCL 社区版", Width = 420, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    var panel = new StackPanel { Spacing = 20, Margin = new Avalonia.Thickness(24) };
                    panel.Children.Add(new TextBlock { Text = (args.Length > 1 ? args[1] : args[0]).Replace("\\n", "\n"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                    var close = new Button { Content = args.ElementAtOrDefault(2) ?? "确定" }; close.Click += (_, _) => dialog.Close(); panel.Children.Add(close); dialog.Content = panel;
                    PrepareDialog(dialog); await dialog.ShowDialog(this); break;
                case "下载文件": case "DownloadFile":
                    var uri = new Uri(args[0]);
                    var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    { Title = "保存主页下载的文件", SuggestedFileName = args.ElementAtOrDefault(2) ?? Path.GetFileName(uri.AbsolutePath) });
                    if (target?.TryGetLocalPath() is { } file)
                    {
                        // A unique sibling forces a fresh download even when the chosen file already exists.
                        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".download";
                        try { await new VerifiedDownloads(_client).FetchAsync(uri.AbsoluteUri, temporary, null, token); File.Move(temporary, file, true); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    break;
                case "今日人品": case "DailyFortune":
                    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_settings.MachineId + DateTime.Today.ToString("yyyyMMdd")));
                    AppendLog("今日人品：" + (BitConverter.ToUInt32(bytes) % 101) + " / 100"); break;
                case "打开帮助": case "OpenHelp": OpenUrl(string.IsNullOrWhiteSpace(data) ? "https://github.com/PCL-Community/PCL-CE" : data); break;
                default: throw new NotSupportedException($"此主页事件尚未兼容：{type}");
            }
        });
    }

    private async Task<bool> ConfirmActionAsync(string title, string text)
    {
        var dialog = new Window { Title = title, Width = 440, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Spacing = 20, Margin = new Avalonia.Thickness(24) };
        content.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 110 });
        var yes = new Button { Content = "继续" }; yes.Click += (_, _) => dialog.Close(true);
        var no = new Button { Content = "取消" }; no.Click += (_, _) => dialog.Close(false);
        content.Children.Add(Actions(yes, no)); dialog.Content = content;
        PrepareDialog(dialog); return await dialog.ShowDialog<bool>(this);
    }

    internal async Task VerifyPagesAsync()
    {
        _animations.IsChecked = true;
        var pages = new[]
        {
            "launch", "download", "packs", "catalog", "instances", "instance-settings", "mods",
            "version-settings", "settings", "appearance", "services", "linux", "wipe", "diagnostics",
            "lobby", "logs", "more"
        };
        foreach (var page in pages)
        {
            Navigate(page);
            await Task.Delay(320);
            VerifyCurrentPage(page == "instance-settings" ? "instances" : page);
        }
        // 快速切页，检查动画中断不会抛异常。
        for (var i = 0; i < 24; i++)
        {
            Navigate(i % 3 == 0 ? "download" : i % 3 == 1 ? "settings" : "launch");
            await Task.Delay(12);
        }
        Navigate("launch");
        await Task.Delay(400);
        VerifyCurrentPage("launch");
        _animations.IsChecked = false;
        await Task.Delay(120);
        Navigate("appearance");
        await Task.Delay(260);
        _animations.IsChecked = true;
        Navigate("launch");
        await Task.Delay(400);
        VerifyCurrentPage("launch");
        var dialogBody = new TextBlock { Text = "圆角弹窗动画检查" };
        var dialog = new Window { Title = "界面检查", Width = 320, Height = 180, Content = dialogBody };
        PrepareDialog(dialog);
        var shown = dialog.ShowDialog(this);
        try
        {
            await Task.Delay(300);
            if (dialog.Content is not Border frame || !dialogBody.IsAttachedToVisualTree())
                throw new InvalidOperationException("弹窗内容未显示。");
        }
        finally { dialog.Close(); }
        await shown;
        if ((_log.Text ?? "").Contains("界面动画已跳过：", StringComparison.Ordinal))
            throw new InvalidOperationException("界面动画发生异常，请查看日志。");
        Console.WriteLine("UI_PAGES_VERIFIED");
        Console.WriteLine($"UI_MOTION_VERIFIED: {pages.Length} pages, transitions, dialog");
    }

    private void VerifyCurrentPage(string page)
    {
        if (_currentPage != page) throw new InvalidOperationException("页面切换未完成：" + page);
        if (_right.Content is null) throw new InvalidOperationException("右侧页面为空：" + page);
        if (Math.Abs(_right.Opacity - 1) > 0.001) throw new InvalidOperationException("页面动画未恢复：" + page);
        foreach (var card in _right.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("card")))
            if (Math.Abs(card.Opacity - 1) > 0.001)
                throw new InvalidOperationException("卡片动画未恢复：" + page);
    }
}
