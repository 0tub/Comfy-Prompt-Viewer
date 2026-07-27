using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;

namespace ComfyPromptViewer;

// Fades a visual's opacity on the render thread instead of with a DoubleTransition on the UI thread.
//
// The gallery fade matters here more than it looks: the UI thread is already running the scroll scheduler
// and dispatching thumbnail decode completions, and a per-tile transition competes with exactly that work.
// A composition implicit animation is handed to the compositor once and interpolated where the frames are
// produced, so a screen full of tiles fading in costs the UI thread nothing.
//
// Usage: set local:CompositionFade.Duration on the visual whose Opacity is bound.
internal static class CompositionFade
{
    public static readonly AttachedProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterAttached<Visual, TimeSpan>("Duration", typeof(CompositionFade));

    private static readonly Easing FadeEasing = new CubicEaseOut();

    static CompositionFade()
    {
        DurationProperty.Changed.AddClassHandler<Visual, TimeSpan>(OnDurationChanged);
    }

    public static void SetDuration(Visual visual, TimeSpan value) => visual.SetValue(DurationProperty, value);

    public static TimeSpan GetDuration(Visual visual) => visual.GetValue(DurationProperty);

    private static void OnDurationChanged(Visual visual, AvaloniaPropertyChangedEventArgs<TimeSpan> args)
    {
        // The composition visual only exists while the control is in a visual tree, and ItemsRepeater
        // recycles these controls in and out, so the animation is installed on every attach rather than once.
        visual.AttachedToVisualTree -= OnAttachedToVisualTree;

        if (args.NewValue.GetValueOrDefault() > TimeSpan.Zero)
        {
            visual.AttachedToVisualTree += OnAttachedToVisualTree;
            TryInstall(visual);
        }
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual)
        {
            TryInstall(visual);
        }
    }

    private static void TryInstall(Visual visual)
    {
        var duration = GetDuration(visual);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            var compositionVisual = ElementComposition.GetElementVisual(visual);
            if (compositionVisual is null)
            {
                return;
            }

            var compositor = compositionVisual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Target = nameof(CompositionVisual.Opacity);
            // Animate from whatever the visual is showing to whatever it was just set to, so the same
            // animation serves fade-in on load and fade-out on recycle.
            animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
            animation.InsertExpressionKeyFrame(1f, "this.FinalValue", FadeEasing);
            animation.Duration = duration;

            var implicitAnimations = compositor.CreateImplicitAnimationCollection();
            implicitAnimations[nameof(CompositionVisual.Opacity)] = animation;
            compositionVisual.ImplicitAnimations = implicitAnimations;
        }
        catch (Exception ex)
        {
            // A missing compositor is a cosmetic loss, not a reason to fail a gallery tile.
            DebugLog.Write($"Failed to install composition fade: {ex.Message}");
        }
    }
}
