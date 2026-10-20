using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FileOrbit.Core.Radial;

namespace FileOrbit.App.Radial;

/// <summary>
/// One slice of the ring: an annular sector that behaves as a normal button (focus, keyboard,
/// UI Automation). Only the painted sector receives clicks.
/// </summary>
public sealed class RadialSegment : ButtonBase
{
    public static readonly DependencyProperty SegmentGeometryProperty = DependencyProperty.Register(nameof(SegmentGeometry), typeof(Geometry), typeof(RadialSegment));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(RadialSegment));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RadialSegment));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RadialSegment));
    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(nameof(GlyphBrush), typeof(Brush), typeof(RadialSegment));
    public static readonly DependencyProperty IsUnavailableProperty = DependencyProperty.Register(nameof(IsUnavailable), typeof(bool), typeof(RadialSegment));
    public static readonly DependencyProperty IsFavoriteProperty = DependencyProperty.Register(nameof(IsFavorite), typeof(bool), typeof(RadialSegment));
    public static readonly DependencyProperty LabelLeftProperty = DependencyProperty.Register(nameof(LabelLeft), typeof(double), typeof(RadialSegment));
    public static readonly DependencyProperty LabelTopProperty = DependencyProperty.Register(nameof(LabelTop), typeof(double), typeof(RadialSegment));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(RadialSegment), new PropertyMetadata(88.0));

    static RadialSegment()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RadialSegment), new FrameworkPropertyMetadata(typeof(RadialSegment)));
    }

    public RadialSegment(RadialItem item, int index)
    {
        Item = item;
        Index = index;
        Focusable = true;
        IsTabStop = true;
        AutomationProperties.SetName(this, item.Caption is null ? item.Label : $"{item.Label}, {item.Caption}");
        AutomationProperties.SetHelpText(this, item.Tooltip);
    }

    public RadialItem Item { get; }

    public int Index { get; }

    /// <summary>Angle of the segment's middle, in degrees clockwise from 12 o'clock.</summary>
    public double MidAngle { get; private set; }

    public Geometry? SegmentGeometry { get => (Geometry?)GetValue(SegmentGeometryProperty); set => SetValue(SegmentGeometryProperty, value); }

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? Caption { get => (string?)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public Brush? GlyphBrush { get => (Brush?)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }

    public bool IsUnavailable { get => (bool)GetValue(IsUnavailableProperty); set => SetValue(IsUnavailableProperty, value); }

    public bool IsFavorite { get => (bool)GetValue(IsFavoriteProperty); set => SetValue(IsFavoriteProperty, value); }

    public double LabelLeft { get => (double)GetValue(LabelLeftProperty); set => SetValue(LabelLeftProperty, value); }

    public double LabelTop { get => (double)GetValue(LabelTopProperty); set => SetValue(LabelTopProperty, value); }

    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    /// <summary>Positions the segment on a ring centred at (<paramref name="center"/>, <paramref name="center"/>).</summary>
    public void Layout(double center, double innerRadius, double outerRadius, double startAngle, double sweepAngle, double gapDegrees)
    {
        var start = startAngle + gapDegrees / 2;
        var sweep = Math.Max(1, sweepAngle - gapDegrees);
        SegmentGeometry = RadialGeometry.AnnularSector(center, innerRadius, outerRadius, start, sweep);
        MidAngle = start + sweep / 2;

        var labelRadius = (innerRadius + outerRadius) / 2;
        var anchor = RadialGeometry.PointAt(center, labelRadius, MidAngle);
        var chord = 2 * labelRadius * Math.Sin(Math.Min(sweep, 180) * Math.PI / 360);
        LabelWidth = Math.Clamp(chord - 8, 56, outerRadius - innerRadius + 12);
        LabelLeft = anchor.X - LabelWidth / 2;
        LabelTop = anchor.Y - 27;
        Width = center * 2;
        Height = center * 2;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RadialSegmentAutomationPeer(this);

    internal void Invoke() => OnClick();
}

/// <summary>Exposes a segment to screen readers and UI Automation as an invokable button.</summary>
internal sealed class RadialSegmentAutomationPeer(RadialSegment owner) : FrameworkElementAutomationPeer(owner), System.Windows.Automation.Provider.IInvokeProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => nameof(RadialSegment);

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    public void Invoke() => ((RadialSegment)Owner).Invoke();
}

public static class RadialGeometry
{
    /// <summary>A point at <paramref name="radius"/> and <paramref name="angleDegrees"/> (0 = top, clockwise).</summary>
    public static Point PointAt(double center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(center + radius * Math.Sin(radians), center - radius * Math.Cos(radians));
    }

    public static Geometry AnnularSector(double center, double innerRadius, double outerRadius, double startAngle, double sweepAngle)
    {
        if (sweepAngle >= 359.9)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(new Point(center, center), outerRadius, outerRadius));
            ring.Children.Add(new EllipseGeometry(new Point(center, center), innerRadius, innerRadius));
            ring.Freeze();
            return ring;
        }

        var end = startAngle + sweepAngle;
        var large = sweepAngle > 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointAt(center, outerRadius, startAngle), isFilled: true, isClosed: true);
            context.ArcTo(PointAt(center, outerRadius, end), new Size(outerRadius, outerRadius), 0, large, SweepDirection.Clockwise, true, true);
            context.LineTo(PointAt(center, innerRadius, end), true, true);
            context.ArcTo(PointAt(center, innerRadius, startAngle), new Size(innerRadius, innerRadius), 0, large, SweepDirection.Counterclockwise, true, true);
        }

        geometry.Freeze();
        return geometry;
    }
}
