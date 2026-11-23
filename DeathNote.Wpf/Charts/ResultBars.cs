using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Models;

namespace DeathNote.Wpf.Charts;

/// <summary>Horizontal comparison chart, no third-party chart package.</summary>
public sealed class ResultBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<CountRow>), typeof(ResultBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnValuesChanged));
    public IEnumerable<CountRow>? Values
    {
        get => (IEnumerable<CountRow>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }
    public static readonly DependencyProperty ShowCivilizationIconsProperty = DependencyProperty.Register(
        nameof(ShowCivilizationIcons), typeof(bool), typeof(ResultBars),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowCivilizationIcons
    {
        get => (bool)GetValue(ShowCivilizationIconsProperty);
        set => SetValue(ShowCivilizationIconsProperty, value);
    }
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.Ordinal);
    private static ImageSource? GetCivIcon(string nation)
    {
        var path = CivilizationEmblems.Icon(nation);
        if (path is null) return null;
        if (IconCache.TryGetValue(path, out var source)) return source;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri("pack://application:,,," + path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            IconCache[path] = bitmap;
            return bitmap;
        }
        catch (Exception) { return null; } // Invalid or missing icon must not crash Statistics.
    }
    private INotifyCollectionChanged? _source;
    private const double RowStep = 54;
    private static readonly Brush[] Palette =
    [
        new SolidColorBrush(Color.FromRgb(185, 105, 142)),
        new SolidColorBrush(Color.FromRgb(142, 108, 157)),
        new SolidColorBrush(Color.FromRgb(194, 143, 120)),
        new SolidColorBrush(Color.FromRgb(113, 155, 145)),
        new SolidColorBrush(Color.FromRgb(132, 122, 144)),
    ];
    private static readonly Brush Track = new SolidColorBrush(Color.FromRgb(54, 43, 56));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(227, 211, 221));
    private static void OnValuesChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ResultBars)obj;
        if (chart._source is not null) chart._source.CollectionChanged -= chart.Changed;
        chart._source = e.NewValue as INotifyCollectionChanged;
        if (chart._source is not null) chart._source.CollectionChanged += chart.Changed;
        chart.InvalidateMeasure();
        chart.InvalidateVisual();
    }
    private void Changed(object? s, NotifyCollectionChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var items = Values?.ToList() ?? [];
        int index = (int)Math.Floor((e.GetPosition(this).Y - 2) / RowStep);
        if (index < 0 || index >= items.Count) { ToolTip = null; return; }
        int count = items[index].Count;
        int total = items.Sum(x => x.Count);
        ToolTip = $"{items[index].Name}: {count} ({(total == 0 ? 0 : count * 100.0 / total):0.0}%)";
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Values?.ToList() ?? [];
        var desiredHeight = items.Count == 0 ? 0 : items.Count * RowStep + 2;
        var desiredWidth = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var items = Values?.ToList() ?? [];
        if(items.Count == 0) return;
        int max = Math.Max(1, items.Max(x => x.Count));
        for(int i = 0; i < items.Count; i++)
        {
            double y = i * RowStep + 2;
            double textStart = 8;
            if (ShowCivilizationIcons && GetCivIcon(items[i].Name) is ImageSource icon)
            {
                dc.DrawImage(icon, new Rect(8, y, 18, 18));
                textStart = 31;
            }
            var f = new FormattedText(items[i].Name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, TextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(50, ActualWidth - textStart - 53), Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(f, new Point(textStart, y));
            var number = new FormattedText(items[i].Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, TextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(number, new Point(ActualWidth - number.Width - 9, y));
            double barY = y + 20;
            double barW = Math.Max(1, ActualWidth - 18);
            dc.DrawRoundedRectangle(Track, null, new Rect(8, barY, barW, 7), 3.5, 3.5);
            dc.DrawRoundedRectangle(Palette[i % Palette.Length], null,
                new Rect(8, barY, barW * items[i].Count / max, 7), 3.5, 3.5);
        }
    }
}
