using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using System.Runtime.CompilerServices;

namespace PCL.Linux;

/// <summary>Short, cancellable animations that always restore the control's resting values.</summary>
internal static class PclMotion
{
    private sealed record RestState(double Opacity, ITransform? Transform);
    private static readonly ConditionalWeakTable<Control, RestState> RestStates = new();
    public static Task EnterAsync(Control control, CancellationToken token, double x = 0, double y = 10,
        int milliseconds = 190, int delay = 0) => RunAsync(control, 0, 1, x, y, 0, 0, milliseconds, delay, token);

    public static Task ExitAsync(Control control, CancellationToken token, double x = -7) =>
        RunAsync(control, 1, 0, 0, 0, x, 0, 70, 0, token);

    private static async Task RunAsync(Control control, double fromOpacity, double toOpacity,
        double fromX, double fromY, double toX, double toY, int milliseconds, int delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rest = RestStates.GetValue(control, target => new RestState(target.Opacity, target.RenderTransform));
        control.RenderTransform = new TranslateTransform();
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds), Delay = TimeSpan.FromMilliseconds(delay),
            Easing = new CubicEaseOut(), FillMode = FillMode.Backward,
            Children =
            {
                Frame(0, fromOpacity, fromX, fromY),
                Frame(1, toOpacity, toX, toY)
            }
        };
        try
        {
            await animation.RunAsync(control, token);
            // Avalonia completes its task before disposing the animation subscription.
            // Let that callback unwind before another transition reads the same properties.
            await Task.Yield();
        }
        finally
        {
            control.SetCurrentValue(Visual.OpacityProperty, rest.Opacity);
            control.SetCurrentValue(Visual.RenderTransformProperty, rest.Transform);
        }
    }

    private static KeyFrame Frame(double cue, double opacity, double x, double y) => new()
    {
        Cue = new Cue(cue), Setters =
        {
            new Setter(Visual.OpacityProperty, opacity),
            new Setter(TranslateTransform.XProperty, x),
            new Setter(TranslateTransform.YProperty, y)
        }
    };

    public static Transitions SlideTransitions(AvaloniaProperty property, bool enabled) => enabled
        ? [new DoubleTransition { Property = property, Duration = TimeSpan.FromMilliseconds(200), Easing = new CubicEaseOut() }]
        : [];
}
