using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeathNote.Wpf.Infrastructure;

public sealed record Civ5TextSegment(string Text, string? Color, string? IconKey = null);

/// <summary>Render Civilization V inline text tags without modifying the stored identities.</summary>
public static class Civ5MarkupText
{
    private static readonly Regex Tags = new(@"\[\s*(?<tag>[A-Za-z0-9_:-]+)\s*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MOVES"] = "➤", ["GOLD"] = "◆", ["FOOD"] = "❧", ["PRODUCTION"] = "⚒",
        ["RESEARCH"] = "⚗", ["CULTURE"] = "♫", ["FAITH"] = "✦", ["RELIGION"] = "✦",
        ["HAPPINESS_1"] = "☺", ["HAPPINESS_2"] = "☻", ["HAPPINESS_3"] = "☹",
        ["HAPPINESS_4"] = "☹", ["STRENGTH"] = "⚔", ["RANGE_STRENGTH"] = "➶",
        ["STAR"] = "★", ["BULLET"] = "•", ["PLUS"] = "+", ["MINUS"] = "−",
        ["TRADE"] = "⇄", ["TRADE_WHITE"] = "⇄", ["GOLDEN_AGE"] = "★",
        ["GREAT_PEOPLE"] = "✦", ["CAPITAL"] = "★", ["CITIZEN"] = "♟",
        ["CITY_STATE"] = "♜", ["SPY"] = "◉", ["INFLUENCE"] = "♛",
        ["PEACE"] = "☮", ["WAR"] = "⚔", ["WORKER"] = "⚒",
        ["CONNECTED"] = "⌁", ["PUPPET"] = "♟", ["LOCKED"] = "🔒",
        ["MISSIONARY"] = "✚", ["PROPHET"] = "✚", ["FLOWER"] = "✿"
    };
    // Exact 22x22 original Civilization V UI symbols supplied by the user.
    // All keys are known at build time; unrecognized tags remain untouched.
    private static readonly HashSet<string> OriginalIconKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALPHA", "BLOCKADED", "BULLET", "CAPITAL", "CITIZEN", 
        "CITY_STATE", "CONNECTED", "CULTURE", "DENOUNCE", "FLOWER", 
        "FOOD", "GOLD", "GOLDEN_AGE", "GREAT_PEOPLE", "HAPPINESS1", 
        "HAPPINESS2", "HAPPINESS3", "HAPPINESS4", "INFLUENCE", "INQUISITOR", 
        "INVEST", "LOCKED", "MINUS", "MISSIONARY", "MOVES", 
        "MUSHROOM", "OCCUPIED", "OMEGA", "PEACE", "PIRATE", 
        "PLUS", "PRODUCTION", "PROPHET", "PUPPET", "RANGE_STRENGTH", 
        "RAZING", "RELIGION", "RELIGION_BUDDHISM", "RELIGION_CHRISTIANITY", "RELIGION_CONFUCIANISM", 
        "RELIGION_HINDUISM", "RELIGION_ISLAM", "RELIGION_JUDAISM", "RELIGION_ORTHODOX", "RELIGION_PANTHEON", 
        "RELIGION_PROTESTANT", "RELIGION_SHINTO", "RELIGION_SIKHISM", "RELIGION_TAOISM", "RELIGION_TENGRIISM", 
        "RELIGION_ZOROASTRIANISM", "RESEARCH", "RESISTANCE", "RES_ALUMINUM", "RES_BANANA", 
        "RES_CITRUS", "RES_COAL", "RES_COPPER", "RES_COTTON", "RES_COW", 
        "RES_CRAB", "RES_DEER", "RES_DYE", "RES_FISH", "RES_FUR", 
        "RES_GEMS", "RES_GOLD", "RES_HORSE", "RES_INCENSE", "RES_IRON", 
        "RES_IVORY", "RES_JEWELRY", "RES_MARBLE", "RES_OIL", "RES_PEARLS", 
        "RES_PORCELAIN", "RES_SALT", "RES_SHEEP", "RES_SILK", "RES_SILVER", 
        "RES_SPICES", "RES_STONE", "RES_SUGAR", "RES_TRUFFLES", "RES_URANIUM", 
        "RES_WHALE", "RES_WHEAT", "RES_WINE", "SPY", "STAR", 
        "STRENGTH", "TEAM1", "TEAM10", "TEAM11", "TEAM2", 
        "TEAM3", "TEAM4", "TEAM5", "TEAM6", "TEAM7", 
        "TEAM8", "TEAM9", "TEAM_USA", "TRADE", "TRADE_WHITE", 
        "VIEW_CITY", "WAR", "WORKER", "WTF1", "WTF2", 
    };

    private static readonly HashSet<string> OriginalIconAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "HAPPINESS_1", "HAPPINESS_2", "HAPPINESS_3", "HAPPINESS_4",
        "TEAM_1", "TEAM_2", "TEAM_3", "TEAM_4", "TEAM_5", "TEAM_6",
        "TEAM_7", "TEAM_8", "TEAM_9", "TEAM_10", "TEAM_11"
    };

    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CYAN"] = "#FF40E5EF", ["RED"] = "#FFFF7878", ["BLUE"] = "#FF76B8FF",
        ["GREEN"] = "#FF70D69C", ["YELLOW"] = "#FFFFD46B", ["GOLD"] = "#FFFFD46B",
        ["WHITE"] = "#FFF4F1F2", ["GREY"] = "#FFB6AFBA", ["GRAY"] = "#FFB6AFBA",
        ["POSITIVE_TEXT"] = "#FF70D69C", ["NEGATIVE_TEXT"] = "#FFFF7878",
        ["WARNING_TEXT"] = "#FFFFD46B", ["MAGENTA"] = "#FFFF8BC8"
    };

    public static IReadOnlyList<Civ5TextSegment> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Array.Empty<Civ5TextSegment>();
        var output = new List<Civ5TextSegment>();
        var colors = new Stack<string?>();
        string? color = null;
        int position = 0;
        void Add(string content)
        {
            if (content.Length == 0) return;
            if (output.Count > 0 && output[^1].IconKey is null && output[^1].Color == color)
                output[^1] = output[^1] with { Text = output[^1].Text + content };
            else output.Add(new Civ5TextSegment(content, color));
        }
        foreach (Match match in Tags.Matches(raw))
        {
            Add(raw[position..match.Index]);
            var tag = match.Groups["tag"].Value.ToUpperInvariant();
            if (tag.StartsWith("COLOR_", StringComparison.Ordinal) &&
                Colors.TryGetValue(tag[6..], out var namedColor))
            {
                colors.Push(color);
                color = namedColor;
            }
            else if (tag.StartsWith("COLOR:", StringComparison.Ordinal) &&
                     TryRgbColor(tag, out var rgbColor))
            {
                colors.Push(color);
                color = rgbColor;
            }
            else if (tag is "ENDCOLOR" or "END_COLOR" or "END_COLOUR")
            {
                color = colors.Count > 0 ? colors.Pop() : null;
            }
            else if (tag == "NEWLINE") Add("\n");
            else if (tag == "TAB") Add("  ");
            else if (tag == "SPACE") Add(" ");
            else if (tag.StartsWith("ICON_", StringComparison.Ordinal))
            {
                var name = tag[5..];
                if (Icons.TryGetValue(name, out var glyph))
                    output.Add(new Civ5TextSegment(glyph, color, name));
                else if (OriginalIconKeys.Contains(name) || OriginalIconAliases.Contains(name))
                    output.Add(new Civ5TextSegment("•", color, name));
                else
                    Add(match.Value); // Preserve unknown icons rather than show a misleading diamond.
            }
            else Add(match.Value); // Unknown bracketed text may be part of the Steam nickname.
            position = match.Index + match.Length;
        }
        Add(raw[position..]);
        return output;
    }

    private static bool TryRgbColor(string tag, out string? color)
    {
        color = null;
        var parts = tag.Split(':');
        if ((parts.Length != 4 && parts.Length != 5) || parts[0] != "COLOR" ||
            !byte.TryParse(parts[1], out var red) || !byte.TryParse(parts[2], out var green) ||
            !byte.TryParse(parts[3], out var blue)) return false;
        byte alpha = 255;
        if (parts.Length == 5 && !byte.TryParse(parts[4], out alpha)) return false;
        color = $"#{alpha:X2}{red:X2}{green:X2}{blue:X2}";
        return true;
    }

    private static readonly Dictionary<string, ImageSource> IconSources = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? GetIconSource(string iconKey)
    {
        if (IconSources.TryGetValue(iconKey, out var cached)) return cached;
        try
        {
            // Only known keys can be used to resolve an embedded application resource.
            if (!Icons.ContainsKey(iconKey) && !OriginalIconKeys.Contains(iconKey) &&
                !OriginalIconAliases.Contains(iconKey)) return null;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/text_icons/{iconKey}.png", UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            IconSources[iconKey] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            // Keep the nickname readable even if an image is missing from the installation.
            return null;
        }
    }

    public static string Plain(string? raw)
    {
        var builder = new StringBuilder();
        foreach (var part in Parse(raw)) builder.Append(part.Text);
        return builder.ToString();
    }

    public static readonly DependencyProperty MarkupProperty = DependencyProperty.RegisterAttached(
        "Markup", typeof(string), typeof(Civ5MarkupText), new PropertyMetadata(string.Empty, OnMarkupChanged));

    public static string GetMarkup(DependencyObject element) => (string)element.GetValue(MarkupProperty);
    public static void SetMarkup(DependencyObject element, string value) => element.SetValue(MarkupProperty, value);

    private static void OnMarkupChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var part in Parse(args.NewValue as string))
        {
            if (part.IconKey is not null && GetIconSource(part.IconKey) is { } source)
            {
                // Preserve the original pixel-art edges (22px source), avoiding repeated blur/upscaling.
                var size = Math.Clamp(Math.Round(block.FontSize * 1.5), 18.0, 22.0);
                var image = new Image
                {
                    Source = source,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
                    ToolTip = $"[ICON_{part.IconKey}]"
                };
                RenderOptions.SetBitmapScalingMode(image,
                    OriginalIconKeys.Contains(part.IconKey) || OriginalIconAliases.Contains(part.IconKey)
                        ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                block.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                continue;
            }
            var run = new Run(part.Text);
            if (part.Color is not null)
            {
                var color = (Color)ColorConverter.ConvertFromString(part.Color)!;
                run.Foreground = new SolidColorBrush(color);
            }
            block.Inlines.Add(run);
        }
    }
}
