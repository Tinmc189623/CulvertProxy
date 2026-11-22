using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Nexsteaduser.CulvertProxy.Core.Engine;
using Windows.Foundation;

namespace Nexsteaduser.CulvertProxy.App.Controls;

/// <summary>轻量吞吐折线图，避免引入图表依赖。</summary>
public sealed partial class TrafficChart : UserControl
{
    private static readonly SolidColorBrush UpStroke = new(Color.FromArgb(255, 45, 212, 191));
    private static readonly SolidColorBrush DownStroke = new(Color.FromArgb(255, 96, 165, 250));
    private static readonly SolidColorBrush GridStroke = new(Color.FromArgb(28, 128, 128, 128));
    private readonly Line[] _gridLines =
    [
        new Line { Stroke = GridStroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } },
        new Line { Stroke = GridStroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } },
        new Line { Stroke = GridStroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } },
    ];
    private readonly Polyline _upLine = new()
    {
        Stroke = UpStroke,
        StrokeThickness = 2.2,
        StrokeLineJoin = PenLineJoin.Round,
    };
    private readonly Polyline _downLine = new()
    {
        Stroke = DownStroke,
        StrokeThickness = 2.2,
        StrokeLineJoin = PenLineJoin.Round,
    };
    private IReadOnlyList<RateSample> _samples = [];
    private bool _visualsReady;

    public TrafficChart()
    {
        InitializeComponent();
    }

    public void SetSamples(IReadOnlyList<RateSample> samples)
    {
        _samples = samples;
        Rebuild();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        EnsureVisuals();

        var top = 6d;
        var bottom = height - 6;
        var plotHeight = Math.Max(bottom - top, 1);

        for (var i = 0; i < _gridLines.Length; i++)
        {
            var line = _gridLines[i];
            var y = top + plotHeight * (i + 1) / 4;
            line.X1 = 0;
            line.X2 = width;
            line.Y1 = y;
            line.Y2 = y;
        }

        var samples = _samples.Count > 120 ? _samples.Skip(_samples.Count - 120).ToArray() : _samples;
        if (samples.Count < 2)
        {
            _upLine.Visibility = Visibility.Collapsed;
            _downLine.Visibility = Visibility.Collapsed;
            if (MaxLabel.Text != "等待流量")
            {
                MaxLabel.Text = "等待流量";
            }

            return;
        }

        var max = 1024d;
        foreach (var sample in samples)
        {
            max = Math.Max(max, Math.Max(sample.UploadBytesPerSecond, sample.DownloadBytesPerSecond));
        }

        var maxText = FormatRate(max);
        if (MaxLabel.Text != maxText)
        {
            MaxLabel.Text = maxText;
        }

        _upLine.Visibility = Visibility.Visible;
        _downLine.Visibility = Visibility.Visible;
        UpdateSeries(_upLine, samples, sample => sample.UploadBytesPerSecond, max, width, top, plotHeight);
        UpdateSeries(_downLine, samples, sample => sample.DownloadBytesPerSecond, max, width, top, plotHeight);
    }

    private void EnsureVisuals()
    {
        if (_visualsReady)
        {
            return;
        }

        _visualsReady = true;
        foreach (var line in _gridLines)
        {
            Plot.Children.Add(line);
        }

        Plot.Children.Add(_upLine);
        Plot.Children.Add(_downLine);
    }

    private static void UpdateSeries(
        Polyline polyline,
        IReadOnlyList<RateSample> samples,
        Func<RateSample, double> selector,
        double max,
        double width,
        double top,
        double plotHeight)
    {
        polyline.Points.Clear();
        for (var i = 0; i < samples.Count; i++)
        {
            var x = width * i / (samples.Count - 1);
            var normalized = Math.Clamp(selector(samples[i]) / max, 0, 1);
            var y = top + plotHeight - (normalized * plotHeight);
            polyline.Points.Add(new Point(x, y));
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024):F1} MiB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:F1} KiB/s";
        }

        return $"{bytesPerSecond:F0} B/s";
    }
}
