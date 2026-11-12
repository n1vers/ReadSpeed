using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StorageBench.Core.Models;

namespace StorageBench.Controls;

/// <summary>
/// График скорости по времени в стиле окна копирования Windows:
/// закрашенная область под линией скорости, сетка и пунктир средней скорости.
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private const double LeftMargin = 56;
    private const double RightMargin = 14;
    private const double TopMargin = 14;
    private const double BottomMargin = 26;
    private const int HorizontalDivisions = 5;
    private const double SmoothingAlpha = 0.22;

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));

    private static readonly Brush FillBrush = Freeze(new LinearGradientBrush(
        Color.FromArgb(0x90, 0x06, 0xB0, 0x25),
        Color.FromArgb(0x25, 0x06, 0xB0, 0x25),
        90));

    private static readonly Pen LinePen = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(0x06, 0xB0, 0x25)), 2)
    {
        LineJoin = PenLineJoin.Round
    });

    private static readonly Pen GridPen = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)), 1));

    private static readonly Pen FramePen = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1));

    private static readonly Pen AveragePen = Freeze(new Pen(
        new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)), 1)
    {
        DashStyle = DashStyles.Dash
    });

    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)));

    private static readonly Typeface GraphTypeface = new("Segoe UI");

    private IReadOnlyList<SpeedSample>? _samples;
    private double? _averageMBps;
    private SpeedUnit _speedUnit = SpeedUnit.MegabytesPerSecond;

    public SpeedGraph()
    {
        SnapsToDevicePixels = true;
    }

    /// <summary>Текст, когда данных ещё нет.</summary>
    public string EmptyText { get; set; } = "Нет данных";

    /// <summary>Задаёт данные графика и перерисовывает его.</summary>
    public void SetData(IReadOnlyList<SpeedSample>? samples, double? averageMBps)
    {
        _samples = samples;
        _averageMBps = averageMBps;
        InvalidateVisual();
    }

    public void SetUnit(SpeedUnit unit)
    {
        _speedUnit = unit;
        InvalidateVisual();
    }

    public void Clear() => SetData(null, null);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        if (width < 120 || height < 80)
            return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var plot = new Rect(
            LeftMargin,
            TopMargin,
            width - LeftMargin - RightMargin,
            height - TopMargin - BottomMargin);

        IReadOnlyList<SpeedSample>? samples = SmoothSamples(_samples);
        bool hasData = samples is { Count: > 0 };

        double maxSpeed = 0;
        double maxTime = 0;

        if (hasData)
        {
            foreach (SpeedSample s in samples!)
            {
                maxSpeed = Math.Max(maxSpeed, ConvertSpeed(s.SpeedMBps));
                maxTime = Math.Max(maxTime, s.TimeSeconds);
            }

            if (_averageMBps is > 0)
                maxSpeed = Math.Max(maxSpeed, ConvertSpeed(_averageMBps.Value));
        }

        double yMax = NiceCeiling(maxSpeed * 1.1);
        double xMax = Math.Max(maxTime, 1);

        double X(double t) => plot.Left + t / xMax * plot.Width;
        double Y(double v) => plot.Bottom - v / yMax * plot.Height;

        DrawGrid(dc, plot, yMax, xMax, X, Y);

        if (hasData)
        {
            DrawSeries(dc, plot, samples!, X, Y);

            if (_averageMBps is > 0)
                DrawAverage(dc, plot, _averageMBps.Value, Y);
        }
        else
        {
            FormattedText empty = MakeText(EmptyText, 13);

            dc.DrawText(empty, new Point(
                plot.Left + (plot.Width - empty.Width) / 2,
                plot.Top + (plot.Height - empty.Height) / 2));
        }

        dc.DrawRectangle(null, FramePen, plot);
    }

    private static IReadOnlyList<SpeedSample>? SmoothSamples(
        IReadOnlyList<SpeedSample>? samples)
    {
        if (samples is not { Count: > 1 })
            return samples;

        var smoothed = new List<SpeedSample>(samples.Count);
        double smoothedSpeed = samples[0].SpeedMBps;

        for (int i = 0; i < samples.Count; i++)
        {
            if (i > 0)
            {
                smoothedSpeed += SmoothingAlpha *
                    (samples[i].SpeedMBps - smoothedSpeed);
            }

            smoothed.Add(new SpeedSample(
                samples[i].TimeSeconds,
                smoothedSpeed));
        }

        return smoothed;
    }

    private void DrawGrid(
        DrawingContext dc,
        Rect plot,
        double yMax,
        double xMax,
        Func<double, double> x,
        Func<double, double> y)
    {
        // Горизонтальные линии и подписи скорости.
        for (int i = 0; i <= HorizontalDivisions; i++)
        {
            double value = yMax * i / HorizontalDivisions;
            double py = y(value);

            if (i > 0 && i < HorizontalDivisions)
                dc.DrawLine(GridPen, new Point(plot.Left, py), new Point(plot.Right, py));

            FormattedText label = MakeText(FormatSpeed(value), 11);

            dc.DrawText(label, new Point(
                plot.Left - 6 - label.Width,
                py - label.Height / 2));
        }

        FormattedText unit = MakeText(SpeedUnitFormatter.GetLabel(_speedUnit), 11);
        dc.DrawText(unit, new Point(2, 0));

        // Вертикальные линии и подписи времени.
        double step = TimeStep(xMax);

        for (double t = 0; t <= xMax + 0.0001; t += step)
        {
            double px = x(t);

            if (t > 0 && px < plot.Right - 1)
                dc.DrawLine(GridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));

            FormattedText label = MakeText($"{t:0.#} с", 11);

            double lx = Math.Min(
                Math.Max(px - label.Width / 2, plot.Left - 4),
                plot.Right - label.Width + 4);

            dc.DrawText(label, new Point(lx, plot.Bottom + 5));
        }
    }

    private void DrawSeries(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<SpeedSample> samples,
        Func<double, double> x,
        Func<double, double> y)
    {
        var area = new StreamGeometry();

        using (StreamGeometryContext ctx = area.Open())
        {
            ctx.BeginFigure(new Point(x(0), plot.Bottom), true, true);
            ctx.LineTo(new Point(x(0), y(ConvertSpeed(samples[0].SpeedMBps))), false, false);

            foreach (SpeedSample s in samples)
                ctx.LineTo(new Point(x(s.TimeSeconds), y(ConvertSpeed(s.SpeedMBps))), false, false);

            ctx.LineTo(new Point(x(samples[samples.Count - 1].TimeSeconds), plot.Bottom), false, false);
        }

        area.Freeze();
        dc.DrawGeometry(FillBrush, null, area);

        var line = new StreamGeometry();

        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(ConvertSpeed(samples[0].SpeedMBps))), false, false);

            foreach (SpeedSample s in samples)
                ctx.LineTo(new Point(x(s.TimeSeconds), y(ConvertSpeed(s.SpeedMBps))), true, true);
        }

        line.Freeze();
        dc.DrawGeometry(null, LinePen, line);
    }

    private void DrawAverage(
        DrawingContext dc,
        Rect plot,
        double average,
        Func<double, double> y)
    {
        double py = y(ConvertSpeed(average));

        if (py < plot.Top || py > plot.Bottom)
            return;

        dc.DrawLine(AveragePen, new Point(plot.Left, py), new Point(plot.Right, py));

        FormattedText label = MakeText(
            $"средняя {FormatSpeed(ConvertSpeed(average))} {SpeedUnitFormatter.GetLabel(_speedUnit)}",
            11);

        double ly = py - label.Height - 2 < plot.Top
            ? py + 2
            : py - label.Height - 2;

        dc.DrawText(label, new Point(plot.Right - label.Width - 6, ly));
    }

    private FormattedText MakeText(string text, double size) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            GraphTypeface,
            size,
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatSpeed(double value) =>
        value >= 100 ? value.ToString("F0")
        : value >= 10 ? value.ToString("0.#")
        : value.ToString("0.0#");

    private double ConvertSpeed(double value) =>
        SpeedUnitFormatter.Convert(value, _speedUnit);

    private static double NiceCeiling(double value)
    {
        if (value <= 0)
            return 1;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double fraction = value / magnitude;

        double nice = fraction <= 1 ? 1
            : fraction <= 2 ? 2
            : fraction <= 5 ? 5
            : 10;

        return nice * magnitude;
    }

    private static double TimeStep(double xMax)
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300 };

        foreach (double step in steps)
        {
            if (xMax / step <= 8)
                return step;
        }

        return 600;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
