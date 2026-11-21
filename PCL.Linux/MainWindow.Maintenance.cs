using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Portable;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    /// <summary>
    /// 清空“配置文件”成功后置为 true。主窗口关闭时 <c>MainWindow.Closing</c> 会调用
    /// <c>SaveSettings()</c> 把内存中的设置重新写入 linux.json，刚清空的配置会立刻复活。
    /// 需要布局/主窗口方在 Closing 中把保存条件改为 <c>else if (!smoke &amp;&amp; !SkipSettingsSaveOnClose)</c>；
    /// 账号令牌不依赖此标志（<see cref="ClearSignedInAccounts"/> 会同步清除内存状态，不会写回 accounts.json）。
    /// </summary>
    internal bool SkipSettingsSaveOnClose { get; private set; }

    /// <summary>
    /// “清理启动器数据”页面：列出各类启动器数据的真实路径与大小，允许任意组合清空，并给出重启建议。
    /// 由布局方注册为 <c>_pages["wipe"] = (Menu("settings"), DataWipePage())</c>。
    /// </summary>
    internal Control DataWipePage()
    {
        var stack = PageStack();
        var entriesHost = new StackPanel { Spacing = 14 };
        var totalText = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var pathsText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        pathsText.Classes.Add("muted");
        var boxes = new List<(CheckBox Box, WipeEntry Entry)>();
        var refreshing = false;
        var wipeButton = new Button { Content = "清空所选数据", VerticalAlignment = VerticalAlignment.Center, IsEnabled = false };
        wipeButton.Classes.Add("pcl");

        string GameDirectory() => string.IsNullOrWhiteSpace(_directory.Text) ? _settings.GameDirectory : _directory.Text.Trim();

        void SetAll(bool value)
        {
            foreach (var (box, _) in boxes)
                if (box.IsEnabled) box.IsChecked = value;
            UpdateSummary();
        }

        void UpdateSummary()
        {
            var selected = boxes.Where(pair => pair.Box.IsChecked == true).Select(pair => pair.Entry).ToArray();
            if (selected.Length == 0)
            {
                totalText.Text = "尚未选择任何类别。";
                pathsText.Text = "";
            }
            else
            {
                totalText.Text = $"已选择 {selected.Length} 类数据，共约 {AppDataWipe.FormatSize(selected.Sum(entry => entry.SizeBytes))}；删除后无法恢复。";
                var lines = selected.SelectMany(entry => entry.Paths.Select(path => $"· {entry.Label}：{path}"));
                pathsText.Text = "实际删除路径：\n" + string.Join('\n', lines);
            }
            wipeButton.IsEnabled = selected.Length > 0 && !refreshing;
        }

        void Populate(IReadOnlyList<WipeEntry> entries)
        {
            entriesHost.Children.Clear();
            boxes.Clear();
            foreach (var entry in entries)
            {
                var label = entry.Exists ? $"{entry.Label}  ·  {AppDataWipe.FormatSize(entry.SizeBytes)}" : $"{entry.Label}  ·  不存在";
                if (entry.Category == WipeCategory.GameData && entry.Exists)
                    label = "【注意】游戏数据（存档 / 模组 / 整合包）  ·  " + AppDataWipe.FormatSize(entry.SizeBytes);
                var box = new CheckBox
                {
                    Content = label,
                    IsChecked = entry.Exists && entry.Category != WipeCategory.GameData,
                    IsEnabled = entry.Exists
                };
                box.IsCheckedChanged += (_, _) => UpdateSummary();
                boxes.Add((box, entry));
                entriesHost.Children.Add(EntryPanel(entry, box));
            }
            if (!entries.Any(entry => entry.Category == WipeCategory.GameData))
            {
                var hint = Paragraph($"当前游戏目录未由启动器管理（缺少 {LinuxPaths.ManagedMarkerName} 标记），因此不会出现在列表中，也不会被删除。");
                hint.FontSize = 12;
                hint.Classes.Add("muted");
                entriesHost.Children.Add(hint);
            }
            UpdateSummary();
        }

        async Task RefreshEntriesAsync()
        {
            if (refreshing) return;
            refreshing = true;
            wipeButton.IsEnabled = false;
            entriesHost.Children.Clear();
            boxes.Clear();
            totalText.Text = "正在统计各类数据占用的空间…";
            pathsText.Text = "";
            var directory = GameDirectory();
            try
            {
                Populate(await Task.Run(() => AppDataWipe.Inspect(directory)));
            }
            catch (Exception ex)
            {
                entriesHost.Children.Clear();
                entriesHost.Children.Add(Paragraph("统计数据大小时出错：" + ex.Message));
                totalText.Text = "";
            }
            finally
            {
                refreshing = false;
                UpdateSummary();
            }
        }

        async Task RequestWipeAsync()
        {
            try
            {
                var chosenCategories = boxes.Where(pair => pair.Box.IsChecked == true)
                    .Select(pair => pair.Entry.Category).ToArray();
                if (chosenCategories.Length == 0)
                {
                    AppendLog("请先选择要清空的数据类别。");
                    return;
                }
                // 游戏运行时 Java 运行时、缓存与游戏目录都可能正在使用，禁止同时清理。
                if (_gameOperation is not null)
                {
                    AppendLog("游戏正在运行，请先退出游戏再清理启动器数据。");
                    return;
                }
                var directory = GameDirectory();
                IReadOnlyList<WipeEntry> fresh;
                try { fresh = await Task.Run(() => AppDataWipe.Inspect(directory)); }
                catch (Exception ex) { AppendLog("统计数据大小失败：" + ex.Message); return; }
                var chosen = fresh.Where(entry => chosenCategories.Contains(entry.Category) && entry.Exists).ToArray();
                if (chosen.Length == 0)
                {
                    AppendLog("所选数据已经不存在，无需清理。");
                    await RefreshEntriesAsync();
                    return;
                }
                // 确认框把“不可恢复”放在第一行，避免长列表把警告挤出弹窗可视区域。
                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"删除后无法恢复。即将清空以下 {chosen.Length} 类启动器数据（共约 {AppDataWipe.FormatSize(chosen.Sum(entry => entry.SizeBytes))}）：");
                foreach (var entry in chosen)
                    summary.AppendLine("· " + entry.Label + "：" + string.Join("、", entry.Paths));
                if (!await ConfirmActionAsync("确认清空数据", summary.ToString().TrimEnd())) return;
                if (chosen.Any(entry => entry.Category == WipeCategory.GameData))
                {
                    var warning = "游戏目录：" + directory +
                        $"\n该目录由启动器管理，其中的存档、模组、整合包都会被删除且无法恢复。\n确认继续吗？";
                    if (!await ConfirmActionAsync("确认删除游戏数据", warning)) return;
                }
                var categories = chosen.Select(entry => entry.Category).ToArray();
                await RunAsync(async token =>
                {
                    var progress = new Progress<string>(message => _status.Text = message);
                    var result = await AppDataWipe.WipeAsync(categories, directory, progress, token);
                    foreach (var path in result.Deleted) AppendLog("已删除：" + path);
                    foreach (var item in result.Skipped) AppendLog("已保留：" + item);
                    foreach (var item in result.Errors) AppendLog("删除失败：" + item);
                    AppendLog($"清空数据完成：释放 {AppDataWipe.FormatSize(result.FreedBytes)}，" +
                        $"删除 {result.Deleted.Count} 项，跳过 {result.Skipped.Count} 项，失败 {result.Errors.Count} 项。");
                    // 令牌文件已经删除时同步清除内存中的登录状态，避免退出时 SaveSettings 又把令牌写回去。
                    if (categories.Contains(WipeCategory.Accounts)) ClearSignedInAccounts();
                    // 配置文件已删除时通知主窗口：关闭时不要再用内存中的设置写回 linux.json。
                    if (categories.Contains(WipeCategory.Config) && !File.Exists(AppDataWipe.ConfigFilePath))
                        SkipSettingsSaveOnClose = true;
                    await RefreshEntriesAsync();
                    await ShowWipeResultAsync(result);
                });
            }
            catch (Exception ex)
            {
                AppendLog("清空数据失败：" + ex.Message);
            }
        }

        async Task ShowWipeResultAsync(WipeResult result)
        {
            var body = new System.Text.StringBuilder();
            body.AppendLine($"已释放 {AppDataWipe.FormatSize(result.FreedBytes)}，删除 {result.Deleted.Count} 项数据。");
            if (result.Skipped.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("以下内容被保留：");
                foreach (var item in result.Skipped.Take(6)) body.AppendLine("· " + item);
                if (result.Skipped.Count > 6) body.AppendLine($"…以及另外 {result.Skipped.Count - 6} 项，详见运行日志。");
            }
            if (result.Errors.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("以下内容未能删除：");
                foreach (var item in result.Errors.Take(6)) body.AppendLine("· " + item);
                if (result.Errors.Count > 6) body.AppendLine($"…以及另外 {result.Errors.Count - 6} 项，详见运行日志。");
            }
            body.AppendLine();
            body.Append("建议立即退出并重新启动启动器，让配置、账号与运行时状态回到初始状态。");
            var dialog = new Window
            {
                Title = "清理完成", Width = 480, Height = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var content = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = body.ToString(), TextWrapping = TextWrapping.Wrap });
            var quit = new Button { Content = "立即退出" };
            quit.Click += (_, _) => dialog.Close(true);
            var later = new Button { Content = "稍后" };
            later.Click += (_, _) => dialog.Close(false);
            content.Children.Add(Actions(quit, later));
            dialog.Content = content;
            PrepareDialog(dialog);
            if (!await dialog.ShowDialog<bool>(this)) return;
            // 退出放在下一帧，等弹窗完全关闭后再结束消息循环。
            Dispatcher.UIThread.Post(QuitLauncher);
        }

        stack.Children.Add(Card("清理启动器数据",
            Paragraph("PCL-CE 按 XDG Base Directory 规范把配置、数据、缓存与状态分别放在 $XDG_CONFIG_HOME、$XDG_DATA_HOME、" +
                "$XDG_CACHE_HOME 与 $XDG_STATE_HOME 下的 pcl-ce 目录中。这里只会删除启动器自己管理的文件与目录，" +
                "不会触碰系统文件、其他应用、用户主目录或其他目录。"),
            entriesHost,
            totalText,
            pathsText,
            Actions(
                InstantButton("全选", () => SetAll(true)),
                InstantButton("全不选", () => SetAll(false)),
                InstantButton("刷新", () => _ = RefreshEntriesAsync()),
                wipeButton)));

        stack.Children.Add(Card("打开数据位置",
            Actions(
                InstantButton("打开配置目录", () => OpenDirectorySafe(LinuxPaths.ConfigHome)),
                InstantButton("打开数据目录", () => OpenDirectorySafe(LinuxPaths.DataHome)),
                InstantButton("打开缓存目录", () => OpenDirectorySafe(LinuxPaths.CacheHome)),
                InstantButton("打开状态目录", () => OpenDirectorySafe(LinuxPaths.StateHome))),
            Paragraph("配置：启动选项、游戏目录、Java、内存与外观设置，以及账号令牌。"),
            Paragraph("数据：自动下载的 Java 运行环境与启动器工具，删除后会在需要时重新下载。"),
            Paragraph("缓存：整合包与资源下载缓存、原生库与临时文件，删除只会影响下次下载速度。"),
            Paragraph("状态与运行时：启动器日志、桌面集成状态、单实例锁与转发 socket；日志可在“运行日志”页面分析。"),
            Paragraph("游戏数据：仅在游戏目录带有 .pcl-ce-managed 标记时才可清理，会同时删除其中的版本、存档、模组与整合包。")));

        var describe = new TextBlock
        {
            Text = "当前路径：\n" + string.Join('\n', LinuxPaths.Describe().Select(pair => $"· {pair.Name}：{pair.Value}")),
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        };
        describe.Classes.Add("muted");
        stack.Children.Add(Card("数据位置（只读）", describe,
            Paragraph("清空操作会立即生效；已经打开的页面或已经登录的账号可能需要重启启动器后才能看到初始状态。")));

        wipeButton.Click += (_, _) => _ = RequestWipeAsync();
        _ = RefreshEntriesAsync();
        return Scroll(stack);
    }

    /// <summary>数据分类的一行：复选框、说明与真实路径（或“不存在”提示）。</summary>
    private static Control EntryPanel(WipeEntry entry, CheckBox box)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(box);
        var description = new TextBlock { Text = entry.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        description.Classes.Add("muted");
        panel.Children.Add(description);
        if (entry.Exists)
        {
            foreach (var path in entry.Paths)
                panel.Children.Add(new TextBlock
                {
                    Text = "· " + path, FontSize = 12, TextWrapping = TextWrapping.Wrap
                });
        }
        else
        {
            var missing = new TextBlock { Text = "路径不存在，无需清理", FontSize = 12 };
            missing.Classes.Add("muted");
            panel.Children.Add(missing);
        }
        return panel;
    }

    /// <summary>不经过 RunAsync 的轻量按钮，用于全选、刷新、打开目录等瞬时操作。</summary>
    private static Button InstantButton(string label, Action action)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("pcl");
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>用 xdg-open 打开启动器数据目录；目录不存在时先按 0700 创建。</summary>
    private void OpenDirectorySafe(string path)
    {
        try
        {
            LinuxPaths.EnsureDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            OpenLocal(path);
        }
        catch (Exception ex)
        {
            AppendLog($"无法打开目录 {path}：{ex.Message}");
        }
    }

    /// <summary>退出启动器；优先结束 Avalonia 桌面生命周期，无法取得生命周期时关闭主窗口。</summary>
    private void QuitLauncher()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            else Close();
        }
        catch (Exception ex)
        {
            AppendLog("退出启动器失败：" + ex.Message);
        }
    }

    /// <summary>账号令牌已随“清空数据”删除时，同步清除内存中的登录状态，避免退出保存时把令牌写回 accounts.json。</summary>
    private void ClearSignedInAccounts()
    {
        _settings.MicrosoftAccount = null;
        _settings.AuthlibAccount = null;
        _settings.UseMicrosoftAccount = false;
        _settings.UseAuthlibAccount = false;
        _microsoft.IsChecked = false;
        _authlib.IsChecked = false;
        RefreshAccount();
    }
}
