using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using DeathNote.Wpf.Models;

namespace DeathNote.Wpf.Charts;

public sealed class MonthlyActivity : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<MonthCountRow>), typeof(MonthlyActivity),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));
    public IEnumerable<MonthCountRow>? Values
    {
        get => (IEnumerable<MonthCountRow>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }
    private INotifyCollectionChanged? _source;
    private static readonly Brush Bar = new SolidColorBrush(Color.FromRgb(166, 93, 123));
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromRgb(213, 143, 173));
    private static readonly Brush Baseline = new SolidColorBrush(Color.FromRgb(64, 49, 63));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(187, 171, 182));
    private static void OnValuesChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var chart = (MonthlyActivity)obj;
        if (chart._source is not null) chart._source.CollectionChanged -= chart.Changed;
        chart._source = e.NewValue as INotifyCollectionChanged;
        if (chart._source is not null) chart._source.CollectionChanged += chart.Changed;
        chart.InvalidateVisual();
    }
    private void Changed(object? s, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var items = Values?.ToList() ?? [];
        if (items.Count == 0 || ActualWidth <= 24) { ToolTip = null; return; }
        double x = e.GetPosition(this).X;
        int i = (int)Math.Floor((x - 12) / ((ActualWidth - 24) / items.Count));
        if (i < 0 || i >= items.Count) { ToolTip = null; return; }
        int count = items[i].Count;
        int total = items.Sum(item => item.Count);
        ToolTip = $"{items[i].Label}: {count} matches ({(total == 0 ? 0 : count * 100.0 / total):0.0}%)";
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var items = Values?.ToList() ?? [];
        if (items.Count == 0) return;
        var left = 12.0;
        var right = Math.Max(left + 1, ActualWidth - 12);
        double top = 27, bottom = Math.Max(top + 18, ActualHeight - 30);
        double height = bottom - top;
        int max = Math.Max(1, items.Max(x => x.Count));
        double step = (right - left) / items.Count;
        dc.DrawLine(new Pen(Baseline, 1), new Point(left, bottom), new Point(right, bottom));
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            double barWidth = Math.Min(36, step * .54);
            double h = height * item.Count / max;
            double x = left + step * (i + .5) - barWidth / 2;
            if (h > 0)
                dc.DrawRoundedRectangle(i == items.Count - 1 ? Highlight : Bar, null,
                    new Rect(x, bottom - h, barWidth, h), 4, 4);
            DrawText(dc, item.Count.ToString(CultureInfo.InvariantCulture), x + barWidth / 2, bottom - h - 21, 11, true);
            DrawText(dc, item.Label, left + step * (i + .5), bottom + 8, 10, true);
        }
    }
    private void DrawText(DrawingContext dc, string text, double x, double y, double size, bool center)
    {
        var f = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(f, new Point(center ? x - f.Width / 2 : x, y));
    }
}
