using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;

namespace PCL.Linux;

internal sealed partial class MainWindow
{
    private CancellationTokenSource? _navigationCancellation;
    private Task _navigationTask = Task.CompletedTask;
    private readonly Border _windowFrame = new();
    private readonly Border _tabIndicator = new()
    {
        Height = 32, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.Parse("#32FFFFFF")),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, IsVisible = false
    };
    private readonly TranslateTransform _tabPosition = new();
    private readonly Dictionary<string, SidebarMenu> _menus = new();
    private readonly CheckBox _animations = new() { Content = "启用界面动画" };
    private readonly ProgressBar _activity = new()
    {
        Height = 3, Width = 58, CornerRadius = new CornerRadius(1.5),
        VerticalAlignment = VerticalAlignment.Center, IsVisible = false
    };
    private bool MotionEnabled => _animations.IsChecked != false;
    private bool _tabPositioned;

    private sealed record SidebarMenu(Grid Root, Dictionary<string, Button> Buttons, Border Indicator, TranslateTransform Position);

    private void InitializeMotion()
    {
        _animations.IsChecked = _settings.EnableAnimations;
        _animations.IsCheckedChanged += (_, _) =>
        {
            ApplyMotionPreference();
            if (!MotionEnabled && _pages.TryGetValue(_currentPage, out var page)) QueuePageChange(page);
        };
        _tabIndicator.RenderTransform = _tabPosition;
        ApplyMotionPreference();
        Opened += (_, _) =>
        {
            UpdateWindowClip(); UpdateTabIndicator();
            if (MotionEnabled && _pages.TryGetValue(_currentPage, out var page)) QueuePageChange(page, entranceOnly: true);
        };
        SizeChanged += (_, _) => { UpdateWindowClip(); UpdateTabIndicator(); };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty || e.Property == ActualTransparencyLevelProperty) UpdateWindowClip(); };
        Closed += (_, _) => _navigationCancellation?.Cancel();
    }

    private void ApplyMotionPreference()
    {
        Classes.Set("motion", MotionEnabled);
        _activity.IsIndeterminate = MotionEnabled;
        _activity.Value = MotionEnabled ? 0 : 40;
        _tabPosition.Transitions = PclMotion.SlideTransitions(TranslateTransform.XProperty, MotionEnabled);
        _tabIndicator.Transitions = PclMotion.SlideTransitions(WidthProperty, MotionEnabled);
        foreach (var menu in _menus.Values)
            menu.Position.Transitions = PclMotion.SlideTransitions(TranslateTransform.YProperty, MotionEnabled);
    }

    private void UpdateWindowClip()
    {
        var radius = WindowState == WindowState.Maximized || ActualTransparencyLevel == WindowTransparencyLevel.None ? 0 : 10;
        _windowFrame.CornerRadius = new CornerRadius(radius);
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
            _windowFrame.Clip = new RectangleGeometry(new Rect(ClientSize), radius, radius);
    }

    private void UpdateTabIndicator()
    {
        var selected = _tabs.Values.FirstOrDefault(button => button.Classes.Contains("selected"));
        if (selected is null || selected.Bounds.Width <= 0) return;
        if (!_tabPositioned)
        {
            _tabPosition.Transitions = []; _tabIndicator.Transitions = [];
        }
        _tabPosition.X = selected.Bounds.X;
        _tabIndicator.Width = selected.Bounds.Width;
        _tabIndicator.IsVisible = true;
        if (!_tabPositioned) { _tabPositioned = true; ApplyMotionPreference(); }
    }

    private void SelectSidebar(string active)
    {
        foreach (var menu in _menus.Values)
        {
            var index = 0;
            foreach (var (id, button) in menu.Buttons)
            {
                button.Classes.Set("selected", id == active);
                if (id == active) menu.Position.Y = 18 + index * 48 + 8;
                index++;
            }
        }
    }

    private void PrepareDialog(Window dialog)
    {
        var body = dialog.Content;
        // Detach before reparenting so the body has exactly one logical owner.
        dialog.Content = null;
        dialog.SystemDecorations = SystemDecorations.None;
        dialog.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        dialog.Background = Brushes.Transparent; dialog.RequestedThemeVariant = RequestedThemeVariant;
        dialog.Classes.Set("motion", MotionEnabled);
        try { Avalonia.Controls.Documents.TextElement.SetFontFamily(dialog, new FontFamily(LinuxFonts.Resolve(_settings.InterfaceFont))); }
        catch (Exception ex) { AppendLog("设置弹窗字体失败：" + ex.Message); }
        foreach (var key in new[] { "PclAccent", "PclAccentHover", "PclAccentPressed", "PclHover", "PclPage", "PclSurface", "PclSidebar", "PclText", "PclMuted", "PclLine" })
            if (Resources.TryGetResource(key, ActualThemeVariant, out var value)) dialog.Resources[key] = value!;
        var layout = new Grid { RowDefinitions = new RowDefinitions("38,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 3, 6, 0) };
        header.Children.Add(new TextBlock { Text = dialog.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = NavigationButton("×", dialog.Close, "dialog-close"); close.BorderThickness = new Thickness(0); close.Padding = new Thickness(9, 3);
        Grid.SetColumn(close, 1); header.Children.Add(close);
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed && e.Source is Visual visual && !visual.GetVisualAncestors().OfType<Button>().Any()) dialog.BeginMoveDrag(e);
        };
        layout.Children.Add(header);
        var host = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(host, 1); layout.Children.Add(host);
        var frame = new Border { Child = layout, CornerRadius = new CornerRadius(10), ClipToBounds = true };
        frame.Bind(Border.BackgroundProperty, dialog.GetResourceObservable("PclSurface"));
        dialog.Content = frame;
        foreach (var button in frame.GetLogicalDescendants().OfType<Button>()) button.Classes.Add("pcl");
        var cancellation = new CancellationTokenSource();
        void UpdateDialogClip()
        {
            var radius = dialog.ActualTransparencyLevel == WindowTransparencyLevel.None ? 0 : 10;
            frame.CornerRadius = new CornerRadius(radius);
            frame.Clip = new RectangleGeometry(new Rect(dialog.ClientSize), radius, radius);
        }
        dialog.SizeChanged += (_, _) => UpdateDialogClip();
        dialog.Opened += async (_, _) =>
        {
            UpdateDialogClip();
            if (!MotionEnabled) return;
            try { await PclMotion.EnterAsync(frame, cancellation.Token, y: 8, milliseconds: 180); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog("界面动画已跳过：" + ex.Message); }
        };
        dialog.Closed += (_, _) => { cancellation.Cancel(); cancellation.Dispose(); };
    }

    private void QueuePageChange((Control Left, Control Right) content, bool entranceOnly = false)
    {
        var previous = _navigationTask;
        _navigationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        _navigationTask = ChangePageAsync(content, previous, cancellation, entranceOnly);
    }

    private async Task ChangePageAsync((Control Left, Control Right) content, Task previous,
        CancellationTokenSource cancellation, bool entranceOnly)
    {
        var token = cancellation.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            var changeLeft = !ReferenceEquals(_left.Child, content.Left);
            var changeRight = !ReferenceEquals(_right.Content, content.Right);
            var animate = MotionEnabled && IsVisible;
            if (animate && !entranceOnly)
            {
                var exit = new List<Task>();
                if (changeRight && _right.Content is not null) exit.Add(PclMotion.ExitAsync(_right, token));
                if (changeLeft && _left.Child is not null) exit.Add(PclMotion.ExitAsync(_left, token, -5));
                await Task.WhenAll(exit);
                token.ThrowIfCancellationRequested();
            }
            if (changeLeft) _left.Child = content.Left;
            if (changeRight) _right.Content = content.Right;
            if (!animate || (!changeLeft && !changeRight && !entranceOnly)) return;
            // Keep cached page instances and scroll positions; animate only the newly displayed content.
            var entry = new List<Task>();
            if (changeLeft || entranceOnly) entry.Add(PclMotion.EnterAsync(_left, token, x: -9, y: 0, milliseconds: 180));
            if (changeRight || entranceOnly)
            {
                entry.Add(PclMotion.EnterAsync(_right, token, x: 12, y: 0, milliseconds: 190));
                var cards = content.Right.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("card")).Take(6).ToArray();
                // Lazy pages have a logical tree before their first layout pass.
                if (cards.Length == 0 && content.Right is ScrollViewer { Content: Panel panel })
                    cards = panel.Children.OfType<Border>().Where(border => border.Classes.Contains("card")).Take(6).ToArray();
                for (var i = 0; i < cards.Length; i++)
                    entry.Add(PclMotion.EnterAsync(cards[i], token, y: 9, milliseconds: 190, delay: 24 * i));
            }
            await Task.WhenAll(entry);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Navigation must stay usable even if an animation is not supported by a renderer.
            if (!token.IsCancellationRequested)
            {
                _left.Child = content.Left; _right.Content = content.Right;
                AppendLog("界面动画已跳过：" + ex.Message);
                if (_smoke) Console.Error.WriteLine(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_navigationCancellation, cancellation)) _navigationCancellation = null;
            cancellation.Dispose();
        }
    }
}
