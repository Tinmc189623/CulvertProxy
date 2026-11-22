using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexsteaduser.CulvertProxy.Core.Diagnostics;

namespace Nexsteaduser.CulvertProxy.App.Pages;

public sealed partial class LogsPage : Page
{
    private const int MaxBuffer = 4000;
    private const int MaxVisible = 1000;
    private static readonly SolidColorBrush TraceBrush = new(Color.FromArgb(255, 148, 163, 184));
    private static readonly SolidColorBrush InfoBrush = new(Color.FromArgb(255, 226, 232, 240));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromArgb(255, 251, 191, 36));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(255, 248, 113, 113));
    private static readonly LogSeverity[] SeverityThresholds = [LogSeverity.Trace, LogSeverity.Info, LogSeverity.Warn, LogSeverity.Error];
    private readonly List<LogRow> _buffer = [];
    private readonly ConcurrentQueue<LogRow> _pending = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _filterTimer;
    private ScrollViewer? _scrollViewer;
    private bool _subscribed;
    private bool _paused;
    private int _threshold;

    public LogsPage()
    {
        InitializeComponent();
        SeverityBox.ItemsSource = new[] { "全部", "信息+", "警告+", "错误" };
        SeverityBox.SelectedIndex = 0;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Drain();
        _filterTimer = DispatcherQueue.CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(250);
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Rebuild();
        };
        Loaded += (_, _) =>
        {
            _scrollViewer = FindScrollViewer(LogList);
            Subscribe();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _filterTimer.Stop();
            _scrollViewer = null;
            Unsubscribe();
        };
    }

    public ObservableCollection<LogRow> Entries { get; } = [];

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        foreach (var entry in App.Services.Log.Snapshot(800))
        {
            _buffer.Add(ToRow(entry));
        }

        TrimBuffer();
        Rebuild();
        App.Services.Log.EntryAdded += OnLogEntry;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        App.Services.Log.EntryAdded -= OnLogEntry;
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (!_paused)
        {
            _pending.Enqueue(ToRow(entry));
        }
    }

    private void Drain()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var added = false;
        for (var i = 0; i < 120 && _pending.TryDequeue(out var row); i++)
        {
            _buffer.Add(row);
            added = true;
            if (Matches(row))
            {
                Entries.Add(row);
                if (Entries.Count > MaxVisible)
                {
                    Entries.RemoveAt(0);
                }
            }
        }

        TrimBuffer();
        if (added)
        {
            CountText.Text = $"{Entries.Count} 条";
            if (AutoScrollToggle.IsOn && Entries.Count > 0 && IsNearBottom())
            {
                LogList.ScrollIntoView(Entries[^1]);
            }
        }
    }

    private bool IsNearBottom()
    {
        var scrollViewer = _scrollViewer;
        return scrollViewer is null
            || scrollViewer.VerticalOffset + scrollViewer.ViewportHeight >= scrollViewer.ScrollableHeight - 48;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void TrimBuffer()
    {
        if (_buffer.Count > MaxBuffer)
        {
            _buffer.RemoveRange(0, _buffer.Count - MaxBuffer);
        }
    }

    private bool Matches(LogRow row)
    {
        var severity = row.Severity;
        if ((int)severity < _threshold)
        {
            return false;
        }

        var keyword = FilterBox.Text.Trim();
        return keyword.Length == 0
            || row.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var row in _buffer.Where(Matches).TakeLast(MaxVisible))
        {
            Entries.Add(row);
        }

        CountText.Text = $"{Entries.Count} 条";
        if (AutoScrollToggle.IsOn && Entries.Count > 0)
        {
            LogList.ScrollIntoView(Entries[^1]);
        }
    }

    private void Severity_Changed(object sender, SelectionChangedEventArgs e)
    {
        _threshold = (int)SeverityThresholds[Math.Max(0, SeverityBox.SelectedIndex)];
        if (IsLoaded)
        {
            Rebuild();
        }
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void Pause_Toggled(object sender, RoutedEventArgs e) => _paused = PauseToggle.IsOn;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        while (_pending.TryDequeue(out _))
        {
        }

        Entries.Clear();
        CountText.Text = "0 条";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{App.Services.LogsPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Services.Log.Warn("Logs", $"打开日志目录失败：{ex.Message}");
        }
    }

    private static LogRow ToRow(LogEntry entry) => new(entry, entry.Severity switch
    {
        LogSeverity.Error => ErrorBrush,
        LogSeverity.Warn => WarnBrush,
        LogSeverity.Info => InfoBrush,
        _ => TraceBrush,
    });
}

/// <summary>日志行。</summary>
public sealed class LogRow
{
    public LogRow(LogEntry entry, Brush brush)
    {
        Time = entry.Timestamp.ToString("HH:mm:ss.fff");
        Level = entry.Severity.ToString().ToUpperInvariant();
        Category = entry.Category;
        Message = entry.ExceptionText is null ? entry.Message : $"{entry.Message} · {entry.ExceptionText.Split('\n')[0]}";
        Severity = entry.Severity;
        Brush = brush;
    }

    public string Time { get; }

    public string Level { get; }

    public string Category { get; }

    public string Message { get; }

    public LogSeverity Severity { get; }

    public Brush Brush { get; }
}
