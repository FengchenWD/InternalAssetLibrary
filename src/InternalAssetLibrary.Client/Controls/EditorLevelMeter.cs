using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace InternalAssetLibrary.Client.Controls;

public sealed class EditorLevelMeter : Control
{
    public static readonly StyledProperty<double> LeftDbProperty =
        AvaloniaProperty.Register<EditorLevelMeter, double>(nameof(LeftDb), -60);

    public static readonly StyledProperty<double> RightDbProperty =
        AvaloniaProperty.Register<EditorLevelMeter, double>(nameof(RightDb), -60);

    static EditorLevelMeter()
    {
        AffectsRender<EditorLevelMeter>(LeftDbProperty, RightDbProperty);
    }

    public double LeftDb
    {
        get => GetValue(LeftDbProperty);
        set => SetValue(LeftDbProperty, value);
    }

    public double RightDb
    {
        get => GetValue(RightDbProperty);
        set => SetValue(RightDbProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 54 || bounds.Height < 120)
        {
            return;
        }

        var primary = Brush("PrimaryTextBrush", Brushes.White);
        var secondary = Brush("SecondaryTextBrush", Brushes.Gray);
        var border = Brush("BorderBrush", Brushes.DimGray);
        var success = Brush("SuccessBrush", Brushes.LimeGreen);
        var warning = Brush("WarningBrush", Brushes.Gold);
        var error = Brush("ErrorBrush", Brushes.Red);
        var surface = Brush("SurfaceRaisedBrush", Brushes.Black);
        var top = 28d;
        var bottom = bounds.Height - 24;
        var meterHeight = Math.Max(1, bottom - top);
        var labelWidth = 30d;
        var gap = 5d;
        var available = Math.Max(18, bounds.Width - labelWidth - gap - 8);
        var barWidth = Math.Max(8, (available - gap) / 2);
        var leftX = labelWidth + gap;
        var rightX = leftX + barWidth + gap;

        context.DrawRectangle(surface, null, new Rect(leftX, top, barWidth, meterHeight));
        context.DrawRectangle(surface, null, new Rect(rightX, top, barWidth, meterHeight));
        foreach (var tick in new[] { 6d, 0, -6, -12, -20, -30, -50 })
        {
            var y = DbToY(tick, top, meterHeight);
            context.DrawLine(new Pen(border, 1), new Point(leftX, y), new Point(rightX + barWidth, y));
            DrawText(context, tick.ToString("0", CultureInfo.InvariantCulture), secondary, 10, 1, y - 7);
        }

        DrawBar(context, LeftDb, leftX, top, barWidth, meterHeight, success, warning, error);
        DrawBar(context, RightDb, rightX, top, barWidth, meterHeight, success, warning, error);
        DrawText(context, FormatPeak(LeftDb), primary, 10, leftX, 5);
        DrawText(context, FormatPeak(RightDb), primary, 10, rightX, 5);
        DrawText(context, "L", secondary, 11, leftX + barWidth / 2 - 3, bottom + 5);
        DrawText(context, "R", secondary, 11, rightX + barWidth / 2 - 3, bottom + 5);
    }

    private static void DrawBar(
        DrawingContext context,
        double db,
        double x,
        double top,
        double width,
        double height,
        IBrush success,
        IBrush warning,
        IBrush error)
    {
        var normalized = Math.Clamp(db, -50, 6);
        var y = DbToY(normalized, top, height);
        var brush = db >= 0 ? error : db >= -6 ? warning : success;
        context.DrawRectangle(brush, null, new Rect(x + 1, y, Math.Max(1, width - 2), top + height - y));
    }

    private static double DbToY(double db, double top, double height) =>
        top + (6 - Math.Clamp(db, -50, 6)) / 56 * height;

    private static string FormatPeak(double db) => db <= -50
        ? "-∞"
        : db.ToString("0", CultureInfo.InvariantCulture);

    private static void DrawText(
        DrawingContext context,
        string text,
        IBrush brush,
        double size,
        double x,
        double y)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush);
        context.DrawText(formatted, new Point(x, y));
    }

    private static IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.Resources[key] as IBrush ?? fallback;
}
