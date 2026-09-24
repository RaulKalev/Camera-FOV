using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Camera_FOV
{
    /// <summary>
    /// Minimal, explanatory motion for surfaces appearing (windows, revealed panels), never for routine updates:
    /// the element fades in and settles from a small offset below. Animating from the current value
    /// (HandoffBehavior.SnapshotAndReplace) keeps it interruptible, so a new reveal never jumps.
    /// With reduced motion ("Show animations in Windows" off) only a short opacity fade remains.
    /// WPF has no spring animation; a short ease-out that settles without overshoot stands in for a
    /// critically damped spring.
    /// </summary>
    public static class Motion
    {
        public static void Reveal(FrameworkElement element, double fromY = 6)
        {
            if (element == null) return;

            bool reduced = ThemeManager.ReducedMotion;
            var duration = TimeSpan.FromMilliseconds(reduced ? 100 : 200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fade = new DoubleAnimation
            {
                From = element.Opacity < 0.99 ? (double?)null : 0.0,
                To = 1.0,
                Duration = duration,
                EasingFunction = ease
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

            if (reduced) return;

            if (!(element.RenderTransform is TranslateTransform translate) || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            // Interrupted mid-slide: continue from the current (presentation) value instead of jumping back.
            bool inFlight = Math.Abs(translate.Y) > 0.01;
            var slide = new DoubleAnimation
            {
                From = inFlight ? (double?)null : fromY,
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            translate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
