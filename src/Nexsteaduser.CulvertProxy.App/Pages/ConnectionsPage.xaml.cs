using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexsteaduser.CulvertProxy.Core.Engine;

namespace Nexsteaduser.CulvertProxy.App.Pages;

public sealed partial class ConnectionsPage : Page
{
    private readonly Dictionary<long, ConnectionRow> _rows = [];
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _filterTimer;

    public ConnectionsPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
        _filterTimer = DispatcherQueue.CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(250);
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Refresh();
        };
    }

    public ObservableCollection<ConnectionRow> Items { get; } = [];

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer.Stop();
        _filterTimer.Stop();
    }

    private void Refresh()
    {
        var snapshots = App.Services.Engine.GetConnections();
        var visible = new List<ConnectionSnapshot>(snapshots.Count);
        var historyKept = 0;

        // 历史记录只渲染最近 300 条，避免长时间运行后每次刷新都遍历并重排整张表。
        foreach (var snapshot in snapshots)
        {
            if (snapshot.IsActive)
            {
                visible.Add(snapshot);
            }
            else if (historyKept < 300)
            {
                visible.Add(snapshot);
                historyKept++;
            }
        }

        var activeOnly = ActiveOnlyToggle.IsOn;
        var filter = FilterBox.Text.Trim();
        var visibleIds = new HashSet<long>();

        foreach (var snapshot in visible)
        {
            if (activeOnly && !snapshot.IsActive)
            {
                continue;
            }

            if (filter.Length > 0 && !Matches(snapshot, filter))
            {
                continue;
            }

            visibleIds.Add(snapshot.Id);
            if (!_rows.TryGetValue(snapshot.Id, out var row))
            {
                row = new ConnectionRow();
                _rows[snapshot.Id] = row;
                Items.Add(row);
            }

            row.Update(snapshot);
        }

        foreach (var id in _rows.Keys.Where(id => !visibleIds.Contains(id)).ToList())
        {
            Items.Remove(_rows[id]);
            _rows.Remove(id);
        }

        SummaryText.Text = $"活动 {snapshots.Count(item => item.IsActive)} · 记录 {snapshots.Count}";
        EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(ConnectionSnapshot snapshot, string filter) =>
        snapshot.Target.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (snapshot.ProcessName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || snapshot.RuleName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (snapshot.Route?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void Filter_Changed(object sender, RoutedEventArgs e) => Refresh();

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionRow row)
        {
            App.Services.Engine.KillConnection(row.Id);
        }

        Refresh();
    }
}

/// <summary>连接列表行，按快照增量更新。</summary>
public sealed partial class ConnectionRow : ObservableObject
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(255, 45, 212, 191));
    private static readonly SolidColorBrush ClosedBrush = new(Color.FromArgb(255, 148, 163, 184));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromArgb(255, 248, 113, 113));
    private static readonly SolidColorBrush BlockedBrush = new(Color.FromArgb(255, 251, 191, 36));

    [ObservableProperty] private string target = string.Empty;
    [ObservableProperty] private string protocol = string.Empty;
    [ObservableProperty] private string stateText = string.Empty;
    [ObservableProperty] private string metaText = string.Empty;
    [ObservableProperty] private string trafficText = string.Empty;
    [ObservableProperty] private string durationText = string.Empty;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private Brush stateBrush = ClosedBrush;

    public long Id { get; private set; }

    public void Update(ConnectionSnapshot snapshot)
    {
        Id = snapshot.Id;
        Target = snapshot.Target;
        Protocol = snapshot.Protocol;
        StateText = snapshot.State switch
        {
            "relaying" => "转发中",
            "connecting" => "连接中",
            "udp-relay" => "UDP 中继",
            "killing" => "断开中",
            "blocked" => "已阻断",
            "failed" => "失败",
            "closed" => "已结束",
            _ => snapshot.State,
        };
        StateBrush = snapshot.State switch
        {
            "relaying" or "udp-relay" => ActiveBrush,
            "failed" => FailedBrush,
            "blocked" => BlockedBrush,
            _ => ClosedBrush,
        };
        MetaText = $"{FormatProcess(snapshot)} · {snapshot.RuleName} · {snapshot.Route ?? "等待链路"}";
        TrafficText = $"↑ {FormatBytes(snapshot.BytesUp)}   ↓ {FormatBytes(snapshot.BytesDown)}";
        DurationText = FormatDuration(TimeSpan.FromMilliseconds(snapshot.ElapsedMs));
        IsActive = snapshot.IsActive;
    }

    private static string FormatProcess(ConnectionSnapshot snapshot) =>
        snapshot.ProcessName is { Length: > 0 } name
            ? snapshot.ProcessId is { } pid ? $"{name} #{pid}" : name
            : "未知进程";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):F2} GiB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):F2} MiB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:F1} KiB" : $"{bytes} B";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
}
