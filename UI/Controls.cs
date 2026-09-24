using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using R2PrismRuntime.Game;

namespace R2PrismRuntime.UI;

/// <summary>Colours of the three prism segment categories and legendaries.</summary>
public static class GemPalette
{
    public static readonly Color Red       = Color.FromRgb(0xB5, 0x45, 0x2E);
    public static readonly Color Blue      = Color.FromRgb(0x3C, 0x7B, 0xA6);
    public static readonly Color Yellow    = Color.FromRgb(0xC7, 0xA0, 0x3A);
    public static readonly Color Legendary = Color.FromRgb(0xE6, 0xCF, 0x8F);
    public static readonly Color None      = Color.FromRgb(0x4A, 0x44, 0x39);

    public static Color For(string category) => category switch
    {
        "Red" => Red,
        "Blue" => Blue,
        "Yellow" => Yellow,
        "Legendary" => Legendary,
        _ => None,
    };

    public static Color Shade(Color c, double f)
        => Color.FromRgb((byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
}

/// <summary>
/// A faceted diamond gem. Fusions are split down the middle into their two colours;
/// legendaries get a pale gold stone with a star facet.
/// </summary>
public sealed class Gem : FrameworkElement
{
    public static readonly DependencyProperty DefProperty = DependencyProperty.Register(
        nameof(Def), typeof(SegmentDef), typeof(Gem),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SegmentDef? Def
    {
        get => (SegmentDef?)GetValue(DefProperty);
        set => SetValue(DefProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? 16 : Width,
        double.IsNaN(Height) ? 16 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var def = Def;
        var top = new Point(w / 2, 0.5);
        var right = new Point(w - 0.5, h / 2);
        var bottom = new Point(w / 2, h - 0.5);
        var left = new Point(0.5, h / 2);
        var center = new Point(w / 2, h / 2);

        Color a, b;
        if (def == null || def.Kind == SegmentKind.Unknown) { a = b = GemPalette.None; }
        else { a = GemPalette.For(def.Primary); b = GemPalette.For(def.Secondary); }

        // Left and right halves, each split into a lit upper and a shaded lower facet.
        Facet(dc, GemPalette.Shade(a, 1.15), top, center, left);
        Facet(dc, GemPalette.Shade(a, 0.70), left, center, bottom);
        Facet(dc, GemPalette.Shade(b, 0.95), top, right, center);
        Facet(dc, GemPalette.Shade(b, 0.55), center, right, bottom);

        if (def?.Kind == SegmentKind.Legendary)
        {
            var star = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xF6, 0xDC)), Math.Max(0.8, w / 20));
            dc.DrawLine(star, new Point(w / 2, h * 0.22), new Point(w / 2, h * 0.78));
            dc.DrawLine(star, new Point(w * 0.22, h / 2), new Point(w * 0.78, h / 2));
        }

        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x09, 0x08)), 1);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(top, false, true);
            g.PolyLineTo(new[] { right, bottom, left }, true, false);
        }
        dc.DrawGeometry(null, outline, geo);

        // Thin highlight on the upper-left edge.
        var hi = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xF4, 0xE0)), 0.8);
        dc.DrawLine(hi, new Point(left.X + w * 0.12, left.Y - h * 0.12), new Point(top.X - w * 0.12, top.Y + h * 0.12));
    }

    private static void Facet(DrawingContext dc, Color c, Point p1, Point p2, Point p3)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(p1, true, true);
            g.PolyLineTo(new[] { p2, p3 }, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(c), null, geo);
    }
}

/// <summary>Row of slanted level ticks. Ticks beyond the live level are drawn in the pending colour.</summary>
public sealed class LevelPips : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(LevelPips), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LiveProperty = DependencyProperty.Register(
        nameof(Live), typeof(int), typeof(LevelPips), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(int), typeof(LevelPips), new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Live { get => (int)GetValue(LiveProperty); set => SetValue(LiveProperty, value); }
    public int Max { get => (int)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

    private static readonly Brush Empty = Frozen(Color.FromRgb(0x38, 0x3C, 0x3E));
    private static readonly Brush Filled = Frozen(Color.FromRgb(0xE6, 0xDF, 0xD2));
    private static readonly Brush Pending = Frozen(Color.FromRgb(0xF0, 0x8A, 0x3C));
    private static readonly Brush Removed = Frozen(Color.FromRgb(0x7A, 0x2A, 0x2E));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override Size MeasureOverride(Size availableSize) => new(Max * 7, 12);

    protected override void OnRender(DrawingContext dc)
    {
        // Grow in steps of ten when a level exceeds the normal cap, keeping the same width.
        int max = Math.Max(1, Max), top = Math.Max(Value, Live);
        if (top > max) max = Math.Min(30, (top + 9) / 10 * 10);
        double h = ActualHeight, step = ActualWidth / max, gap = max > 10 ? 1.5 : 3;
        double tick = Math.Max(1.5, step - gap), slant = max > 10 ? 2 : 3;
        for (int i = 0; i < max; i++)
        {
            bool inValue = i < Value, inLive = i < Live;
            Brush b = inValue && inLive ? Filled : inValue ? Pending : inLive ? Removed : Empty;
            double x = i * step;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x + slant, 0), true, true);
                g.PolyLineTo(new[] { new Point(x + slant + tick, 0), new Point(x + tick, h), new Point(x, h) }, false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(b, null, geo);
        }
    }
}

/// <summary>{ui:Caps Some Text} → letter-spaced upper-case label.</summary>
[System.Windows.Markup.MarkupExtensionReturnType(typeof(string))]
public sealed class CapsExtension : System.Windows.Markup.MarkupExtension
{
    public string Text { get; set; } = "";
    public bool Wide { get; set; }
    public CapsExtension() { }
    public CapsExtension(string text) => Text = text;
    public override object ProvideValue(IServiceProvider sp)
        => new TrackedConverter().Convert(Text, typeof(string), Wide ? "wide" : "", CultureInfo.InvariantCulture);
}

// ══ Converters ═══════════════════════════════════════════════════

/// Upper-cases text and spreads the letters, like the game's menu headers.
public sealed class TrackedConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        string s = (value?.ToString() ?? "").ToUpperInvariant();
        string gap = parameter as string == "wide" ? "  " : " ";
        return string.Join(gap, s.ToCharArray());
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value != null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is int n && n > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// value.ToString() == parameter → Visible/Collapsed (or true/false when targeting bool).
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool eq = string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
        return t == typeof(Visibility) ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// True when all bound values are equal as strings (used for the active filter chip).
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
        => values.Length > 1 && values.All(v => string.Equals(v?.ToString(), values[0]?.ToString(), StringComparison.Ordinal));
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string key = value switch
        {
            Game.HookState.On => "GoodBrush",
            Game.HookState.Off => "TextFaintBrush",
            Game.HookState.Foreign or Game.HookState.Unavailable => "BadBrush",
            Tone.Good => "GoodBrush",
            Tone.Warn => "WarnBrush",
            Tone.Bad => "BadBrush",
            LinkState.Linked => "GoodBrush",
            LinkState.Loading => "WarnBrush",
            LinkState.Error => "BadBrush",
            _ => "TextFaintBrush",
        };
        return Application.Current.FindResource(key);
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// Float XP with thousands separators; parses both "1234.5" and "1234,5".
public sealed class XpConverter : IValueConverter
{
    public bool AllowNegative { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        string s = (value as string ?? "").Trim().Replace(" ", "").Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
               && (AllowNegative || f >= 0) && float.IsFinite(f)
            ? f
            : DependencyProperty.UnsetValue;
    }
}
