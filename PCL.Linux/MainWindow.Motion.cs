using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PCL.Linux;

/// <summary>窗口动画状态、圆角裁切与 PCL 风格弹窗。</summary>
internal sealed partial class MainWindow
{
    private readonly CheckBox _animations = new() { Content = "启用界面动画" };
    private bool _motionEnabled = true;

    private bool MotionEnabled => _motionEnabled;

    private void InitializeMotion()
    {
        _animations.IsChecked = _settings.EnableAnimations;
        _motionEnabled = _settings.EnableAnimations;
        _animations.IsCheckedChanged += (_, _) =>
        {
            _motionEnabled = _animations.IsChecked != false;
            Classes.Set("motion", _motionEnabled);
            _activity.IsIndeterminate = _motionEnabled;
        };
        Opened += (_, _) => { UpdateWindowClip(); };
        SizeChanged += (_, _) => UpdateWindowClip();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateWindowClip();
        };
    }

    private void UpdateWindowClip()
    {
        var radius = WindowState == WindowState.Maximized || _settings.UseSystemTitlebar ? 0 : 6;
        _windowFrame.CornerRadius = new CornerRadius(radius);
        _windowFrame.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(10);
    }

    /// <summary>PCL CE 风格弹窗：圆角、主题背景、强调色标题栏与白色标题，底部按钮靠右。</summary>
    private void PrepareDialog(Window dialog)
    {
        var body = dialog.Content;
        dialog.Content = null;
        dialog.SystemDecorations = SystemDecorations.None;
        dialog.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        dialog.Background = Brushes.Transparent;
        dialog.RequestedThemeVariant = RequestedThemeVariant;
        dialog.Classes.Set("motion", MotionEnabled);
        try { Avalonia.Controls.Documents.TextElement.SetFontFamily(dialog, new FontFamily(LinuxFonts.Resolve(_settings.InterfaceFont))); }
        catch (Exception ex) { AppendLog("设置弹窗字体失败：" + ex.Message); }
        foreach (var key in new[]
                 {
                     "ColorBrush1", "ColorBrush2", "ColorBrush3", "ColorBrush4", "ColorBrush5", "ColorBrush6",
                     "ColorBrush7", "ColorBrush8", "ColorBrushTransparentBackground", "ColorBrushBackground",
                     "ColorBrushGray1", "ColorBrushGray2", "ColorBrushGray3", "ColorBrushGray4",
                     "ColorBrushWhite", "ColorBrushHalfWhite", "ColorBrushBg1"
                 })
            if (Resources.TryGetResource(key, ActualThemeVariant, out var value)) dialog.Resources[key] = value!;

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = PclUi.Brush("ColorBrushBackground"),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.Parse("#40000000") })
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("34,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 34 };
        header.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(PclColors.GetColor("ColorBrush2"), 0),
                new GradientStop(PclColors.GetColor("ColorBrush3"), 1)
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = dialog.Title, Foreground = Brushes.White, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0)
        });
        var close = PclUi.IconButton("x", 26, white: true);
        close.Margin = new Thickness(0, 0, 5, 0);
        close.Click += (_, _) => dialog.Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);
        var host = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(20, 16, 20, 18)
        };
        Grid.SetRow(host, 1);
        layout.Children.Add(host);
        frame.Child = layout;
        dialog.Content = frame;
        dialog.Padding = new Thickness(10);
        dialog.Opened += async (_, _) =>
        {
            if (!MotionEnabled) return;
            try { await PclMotion.EnterAsync(frame, CancellationToken.None, y: 8, milliseconds: 180); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog("界面动画已跳过：" + ex.Message); }
        };
    }
}

