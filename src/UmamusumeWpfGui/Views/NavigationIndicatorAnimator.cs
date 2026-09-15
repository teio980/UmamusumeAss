using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Reproduces the WinUI/qfluentwidgets squash-and-stretch indicator motion.
///
/// The reference implementation's ScaleSlideAnimation is a 200 ms + 400 ms
/// sequential animation. The first phase stretches the indicator by the
/// travel distance; the second phase moves it to the destination while
/// returning it to its normal length. Keeping one indicator per host is what
/// makes the motion read as a single fluent visual rather than two fades.
/// </summary>
internal static class NavigationIndicatorAnimator
{
    private const double StretchPhaseMilliseconds = 200;
    private const double SettlePhaseMilliseconds = 400;
    private const double TotalMilliseconds =
        StretchPhaseMilliseconds + SettlePhaseMilliseconds;

    private static readonly Dictionary<FrameworkElement, Storyboard> ActiveTransitions = new();

    public static bool IsAnimating(FrameworkElement indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        return ActiveTransitions.ContainsKey(indicator);
    }

    /// <summary>
    /// Positions a shared indicator. When <paramref name="animate"/> is true,
    /// the fixed-axis geometry is animated with the same two-stage morph used
    /// by March7thAssistant's qfluentwidgets 1.11.2 ScaleSlideAnimation.
    /// </summary>
    public static void SetBounds(
        FrameworkElement indicator,
        Rect target,
        bool animate)
    {
        ArgumentNullException.ThrowIfNull(indicator);

        var current = ReadBounds(indicator);
        var hasCurrentBounds = IsFinite(current.Left)
            && IsFinite(current.Top)
            && IsFinite(current.Width)
            && IsFinite(current.Height)
            && current.Width >= 0
            && current.Height >= 0
            && indicator.Visibility == Visibility.Visible;

        indicator.Visibility = Visibility.Visible;

        if (!animate || !hasCurrentBounds)
        {
            ApplyBounds(indicator, target);
            return;
        }

        var axis = ResolveAxis(current, target);
        if (axis is null)
        {
            // This is only expected while a template/layout is changing. A
            // regular root navigation or settings-tab transition is always
            // axis-aligned, just like the reference control.
            ApplyBounds(indicator, target);
            return;
        }

        var (from, to, dimension) = axis == IndicatorAxis.Horizontal
            ? (current.Left, target.Left, current.Width)
            : (current.Top, target.Top, current.Height);
        var distance = Math.Abs(to - from);

        if (distance < 0.01
            && Math.Abs(target.Width - current.Width) < 0.01
            && Math.Abs(target.Height - current.Height) < 0.01)
        {
            ApplyBounds(indicator, target);
            return;
        }

        StopActiveTransition(indicator);

        var stretchedLength = dimension + distance;
        var isForward = to > from;

        // qfluentwidgets uses the same cubic Bezier curves for every
        // ScaleSlideAnimation instance: fast stretch, soft settle.
        var stretchSpline = new KeySpline(0.9, 0.1, 1.0, 0.2);
        var settleSpline = new KeySpline(0.1, 0.9, 0.2, 1.0);

        var storyboard = new Storyboard();
        var positionAnimation = CreateMorphAnimation(
            isForward
                ? new[] { from, from, to }
                : new[] { from, to, to },
            stretchSpline,
            settleSpline);
        var lengthAnimation = CreateMorphAnimation(
            new[] { dimension, stretchedLength, dimension },
            stretchSpline,
            settleSpline);

        if (axis == IndicatorAxis.Horizontal)
        {
            AddAnimation(storyboard, indicator, Canvas.LeftProperty, positionAnimation);
            AddAnimation(storyboard, indicator, FrameworkElement.WidthProperty, lengthAnimation);
            AddAnimation(
                storyboard,
                indicator,
                Canvas.TopProperty,
                CreateConstantAnimation(current.Top, target.Top));
            AddAnimation(
                storyboard,
                indicator,
                FrameworkElement.HeightProperty,
                CreateConstantAnimation(current.Height, target.Height));
        }
        else
        {
            AddAnimation(storyboard, indicator, Canvas.TopProperty, positionAnimation);
            AddAnimation(storyboard, indicator, FrameworkElement.HeightProperty, lengthAnimation);
            AddAnimation(
                storyboard,
                indicator,
                Canvas.LeftProperty,
                CreateConstantAnimation(current.Left, target.Left));
            AddAnimation(
                storyboard,
                indicator,
                FrameworkElement.WidthProperty,
                CreateConstantAnimation(current.Width, target.Width));
        }

        ActiveTransitions[indicator] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ActiveTransitions.TryGetValue(indicator, out var active)
                || !ReferenceEquals(active, storyboard))
            {
                return;
            }

            ActiveTransitions.Remove(indicator);
            // HoldEnd keeps the animation layer alive after Completed. Remove
            // it before committing the final base values, otherwise a later
            // resize can be hidden by the completed clock.
            storyboard.Remove(indicator);
            SetBaseBounds(indicator, target);
        };

        // Start at the current visual value. The caller reads that value
        // before stopping a previous transition, so rapid switching remains
        // continuous instead of jumping back to the previous tab.
        storyboard.Begin(indicator, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private static Rect ReadBounds(FrameworkElement indicator) => new(
        Canvas.GetLeft(indicator),
        Canvas.GetTop(indicator),
        indicator.Width,
        indicator.Height);

    private static IndicatorAxis? ResolveAxis(Rect current, Rect target)
    {
        var horizontalTravel = Math.Abs(target.Left - current.Left);
        var verticalTravel = Math.Abs(target.Top - current.Top);
        var horizontalSizeChange = Math.Abs(target.Width - current.Width);
        var verticalSizeChange = Math.Abs(target.Height - current.Height);

        var horizontal = verticalSizeChange < 0.01
            && (horizontalTravel >= verticalTravel || horizontalSizeChange > 0.01);
        var vertical = horizontalSizeChange < 0.01
            && (verticalTravel > horizontalTravel || verticalSizeChange > 0.01);

        if (horizontal && !vertical)
            return IndicatorAxis.Horizontal;
        if (vertical && !horizontal)
            return IndicatorAxis.Vertical;

        // Equal travel with both dimensions unchanged is still valid for the
        // settings underline; prefer the axis whose normal length is wider.
        if (horizontal && vertical)
            return current.Width >= current.Height
                ? IndicatorAxis.Horizontal
                : IndicatorAxis.Vertical;

        return null;
    }

    private static DoubleAnimationUsingKeyFrames CreateMorphAnimation(
        double[] values,
        KeySpline stretchSpline,
        KeySpline settleSpline)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(TotalMilliseconds)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            values[0],
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            values[1],
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(StretchPhaseMilliseconds)),
            stretchSpline));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            values[2],
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TotalMilliseconds)),
            settleSpline));
        return animation;
    }

    private static DoubleAnimation CreateConstantAnimation(double from, double to) =>
        new()
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(TotalMilliseconds)),
            FillBehavior = FillBehavior.HoldEnd,
        };

    private static void AddAnimation(
        Storyboard storyboard,
        FrameworkElement target,
        DependencyProperty property,
        Timeline animation)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private static void ApplyBounds(FrameworkElement indicator, Rect bounds)
    {
        StopActiveTransition(indicator);
        SetBaseBounds(indicator, bounds);
    }

    private static void SetBaseBounds(FrameworkElement indicator, Rect bounds)
    {
        Canvas.SetLeft(indicator, bounds.Left);
        Canvas.SetTop(indicator, bounds.Top);
        indicator.Width = bounds.Width;
        indicator.Height = bounds.Height;
    }

    private static void StopActiveTransition(FrameworkElement indicator)
    {
        if (ActiveTransitions.Remove(indicator, out var storyboard))
            storyboard.Remove(indicator);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private enum IndicatorAxis
    {
        Horizontal,
        Vertical,
    }
}
