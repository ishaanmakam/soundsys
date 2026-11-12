using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Soundsys.App.ViewModels;

namespace Soundsys.App.Controls;

/// <summary>
/// The signature control: one ring per speaker. Apart and outlined when stopped; slid together into an amber lens when
/// playing (260 ms ease-out, only on state change, skipped when Windows animations are off). Each ring shows its
/// speaker's live state: amber = playing, breathing outline = connecting, dashed alert = lost, grey = off.
/// </summary>
public sealed class TwoRings : FrameworkElement
{
    public const double MergeMilliseconds = 260;

    public static readonly DependencyProperty StateAProperty = DependencyProperty.Register(
        nameof(StateA), typeof(RingVisual), typeof(TwoRings),
        new FrameworkPropertyMetadata(RingVisual.Off, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty StateBProperty = DependencyProperty.Register(
        nameof(StateB), typeof(RingVisual), typeof(TwoRings),
        new FrameworkPropertyMetadata(RingVisual.Off, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty IsMergedProperty = DependencyProperty.Register(
        nameof(IsMerged), typeof(bool), typeof(TwoRings), new FrameworkPropertyMetadata(false, OnMergedChanged));

    /// <summary>0 = apart, 1 = merged (animated).</summary>
    public static readonly DependencyProperty MergeProgressProperty = DependencyProperty.Register(
        nameof(MergeProgress), typeof(double), typeof(TwoRings),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Opacity of breathing rings (animated while any ring is connecting or calibrating).</summary>
    public static readonly DependencyProperty BreathProperty = DependencyProperty.Register(
        nameof(Breath), typeof(double), typeof(TwoRings),
        new FrameworkPropertyMetadata(StaticBreath, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SignalBrushProperty = BrushProperty(nameof(SignalBrush));
    public static readonly DependencyProperty SignalSoftBrushProperty = BrushProperty(nameof(SignalSoftBrush));
    public static readonly DependencyProperty MutedBrushProperty = BrushProperty(nameof(MutedBrush));
    public static readonly DependencyProperty AlertBrushProperty = BrushProperty(nameof(AlertBrush));

    private const double StaticBreath = 0.6;
    private bool _breathing;

    public TwoRings()
    {
        Loaded += (_, _) => UpdateBreathing();
        Unloaded += (_, _) => StopBreathing();
    }

    /// <summary>Tests and screenshots: render final states with no motion.</summary>
    internal static bool ForceReducedMotion { get; set; }

    public RingVisual StateA
    {
        get => (RingVisual)GetValue(StateAProperty);
        set => SetValue(StateAProperty, value);
    }

    public RingVisual StateB
    {
        get => (RingVisual)GetValue(StateBProperty);
        set => SetValue(StateBProperty, value);
    }

    public bool IsMerged
    {
        get => (bool)GetValue(IsMergedProperty);
        set => SetValue(IsMergedProperty, value);
    }

    public double MergeProgress
    {
        get => (double)GetValue(MergeProgressProperty);
        set => SetValue(MergeProgressProperty, value);
    }

    public double Breath
    {
        get => (double)GetValue(BreathProperty);
        set => SetValue(BreathProperty, value);
    }

    public Brush? SignalBrush
    {
        get => (Brush?)GetValue(SignalBrushProperty);
        set => SetValue(SignalBrushProperty, value);
    }

    public Brush? SignalSoftBrush
    {
        get => (Brush?)GetValue(SignalSoftBrushProperty);
        set => SetValue(SignalSoftBrushProperty, value);
    }

    public Brush? MutedBrush
    {
        get => (Brush?)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    public Brush? AlertBrush
    {
        get => (Brush?)GetValue(AlertBrushProperty);
        set => SetValue(AlertBrushProperty, value);
    }

    private static bool MotionAllowed => !ForceReducedMotion && SystemParameters.ClientAreaAnimation;

    private static bool Breathes(RingVisual v) => v is RingVisual.Connecting or RingVisual.Calibrating;

    protected override Size MeasureOverride(Size availableSize) => new(200, 100);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        const double stroke = 3;
        var diameter = Math.Min(h - (stroke * 2), w * 0.48);
        if (diameter <= 4)
        {
            return;
        }

        var r = diameter / 2;
        var p = Math.Clamp(MergeProgress, 0, 1);

        // Apart: edges 8 px apart. Merged: centres half a diameter apart (a clear lens).
        var apart = diameter + 8;
        var merged = diameter * 0.52;
        var sep = apart + ((merged - apart) * p);
        var cy = h / 2;
        var ca = new Point((w / 2) - (sep / 2), cy);
        var cb = new Point((w / 2) + (sep / 2), cy);

        DrawFill(dc, StateA, ca, r);
        DrawFill(dc, StateB, cb, r);

        // The lens: two speakers, one sound. Solid amber once both play together.
        if (StateA == RingVisual.Playing && StateB == RingVisual.Playing && p > 0.01 && SignalBrush is not null)
        {
            var lens = new CombinedGeometry(GeometryCombineMode.Intersect, new EllipseGeometry(ca, r, r), new EllipseGeometry(cb, r, r));
            dc.PushOpacity(0.92 * p);
            dc.DrawGeometry(SignalBrush, null, lens);
            dc.Pop();
        }

        DrawRing(dc, StateA, ca, r, stroke);
        DrawRing(dc, StateB, cb, r, stroke);
    }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(TwoRings), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TwoRings)d).UpdateBreathing();

    private static void OnMergedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var rings = (TwoRings)d;
        var to = (bool)e.NewValue ? 1.0 : 0.0;
        if (!MotionAllowed || !rings.IsLoaded)
        {
            rings.BeginAnimation(MergeProgressProperty, null);
            rings.MergeProgress = to;
            return;
        }

        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(MergeMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        rings.BeginAnimation(MergeProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateBreathing()
    {
        var want = (Breathes(StateA) || Breathes(StateB)) && MotionAllowed && IsLoaded;
        if (want == _breathing)
        {
            return;
        }

        if (want)
        {
            _breathing = true;
            var animation = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            BeginAnimation(BreathProperty, animation);
        }
        else
        {
            StopBreathing();
        }
    }

    private void StopBreathing()
    {
        _breathing = false;
        BeginAnimation(BreathProperty, null);
        Breath = StaticBreath;
    }

    private void DrawFill(DrawingContext dc, RingVisual state, Point c, double r)
    {
        if (SignalSoftBrush is null)
        {
            return;
        }

        switch (state)
        {
            case RingVisual.Playing:
                dc.DrawEllipse(SignalSoftBrush, null, c, r, r);
                break;
            case RingVisual.Calibrating:
                dc.PushOpacity(Breath);
                dc.DrawEllipse(SignalSoftBrush, null, c, r, r);
                dc.Pop();
                break;
        }
    }

    private void DrawRing(DrawingContext dc, RingVisual state, Point c, double r, double stroke)
    {
        var (brush, thickness, opacity, dashed) = state switch
        {
            RingVisual.Off => (MutedBrush, 2.0, 0.4, false),
            RingVisual.Idle => (MutedBrush, 2.5, 1.0, false),
            RingVisual.Connecting => (MutedBrush, 2.5, Breath, false),
            RingVisual.Waiting => (SignalBrush, stroke, 1.0, false),
            RingVisual.Playing => (SignalBrush, stroke, 1.0, false),
            RingVisual.Lost => (AlertBrush, 2.5, 1.0, true),
            RingVisual.Calibrating => (SignalBrush, stroke, Breath, false),
            _ => (MutedBrush, 2.0, 1.0, false),
        };
        if (brush is null)
        {
            return;
        }

        var pen = new Pen(brush, thickness);
        if (dashed)
        {
            pen.DashStyle = new DashStyle([2.2, 1.6], 0);
            pen.DashCap = PenLineCap.Round;
        }

        // Keep the stroke inside the ring's radius so apart/merged widths stay exact.
        var rr = r - (thickness / 2);
        dc.PushOpacity(opacity);
        dc.DrawEllipse(null, pen, c, rr, rr);
        dc.Pop();
    }
}
