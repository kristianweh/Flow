using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bansa.Models;
using Bansa.Services;

namespace Bansa.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly NetworkMonitor _monitor = new();
    private readonly HistoryStore _history = new();
    // Initialized in constructor (after _monitor) — DownloadThrottler holds a reference
    // to NetworkMonitor so its 100 ms InnerTick can read raw byte counters directly.
    private readonly DownloadThrottler _downloadThrottler;
    private PingMonitor _ping;                                      // non-readonly: can be replaced when target changes
    // 7 200 samples × 0.5 s = 1 hour of scrollable history in the main chart.
    private const int kChartCapacity = 7200;
    private readonly RateHistory _rateHistory = new(capacity: kChartCapacity);

    // Per-tick app snapshots, in lockstep with _rateHistory.
    // Each slot holds the top-5 active apps visible at that moment (or null if idle).
    // Includes ImagePath so the crosshair tooltip can show app icons.
    private readonly (string Name, string ImagePath, long DownBps, long UpBps)[]?[] _appAtTick
        = new (string, string, long, long)[]?[kChartCapacity];
    private int _appAtTickHead;
    private int _appAtTickCount;
    private int _hourlyRefreshCounter;                              // throttle hourly DB queries
    // Set when an app transitions zero-traffic → active this tick so we do a targeted
    // filter Refresh() to make it visible. Avoids the expensive unconditional Refresh().
    private bool _needsFilterRefresh;
    private readonly Dictionary<string, AppRowViewModel> _rowsByName =
        new(StringComparer.OrdinalIgnoreCase);
    // Track which image paths / names have already had their saved limits restored (once per session)
    private readonly HashSet<string> _restoredLimitPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRollup = DateTime.UtcNow;

    // Today's usage — refreshed from the history DB every ~10 s
    private long _todayBytesIn;
    private long _todayBytesOut;
    private int  _todayRefreshCounter;

    public ObservableCollection<AppRowViewModel> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    [ObservableProperty] private string statusText = "Starting…";
    [ObservableProperty] private long totalDownBps;
    [ObservableProperty] private long totalUpBps;
    [ObservableProperty] private string filterText = "";
    [ObservableProperty] private double hideBelowKBps;
    [ObservableProperty] private bool isDarkTheme;
    [ObservableProperty] private int pingMs = -1;
    [ObservableProperty] private string pingStatus = "—";
    [ObservableProperty] private bool useBitsUnit;
    [ObservableProperty] private int trayIconSize;
    [ObservableProperty] private bool useWindowsAccent;
    [ObservableProperty] private int globalUploadCapKBs;
    [ObservableProperty] private bool isGlobalUploadCapEnabled;
    [ObservableProperty] private bool isGlobalUploadCapPersistent;
    [ObservableProperty] private bool isScenarioActive;
    [ObservableProperty] private bool showFloatingGraph;
    [ObservableProperty] private AppRowViewModel? selectedApp;
    [ObservableProperty] private bool hideLocalOnlyApps;
    /// <summary>
    /// Non-empty when the QoS Packet Scheduler is not bound to an active network adapter.
    /// Shown as an actionable warning banner in the Global Upload Cap / Scenarios settings card.
    /// Empty string = QoS is healthy (or no global cap is configured).
    /// </summary>
    [ObservableProperty] private string qosWarning = "";

    public bool QosWarningVisible => !string.IsNullOrEmpty(QosWarning);
    partial void OnQosWarningChanged(string value) => OnPropertyChanged(nameof(QosWarningVisible));

    /// <summary>Assembly version (from the csproj &lt;Version&gt;), shown in Settings → General.</summary>
    public string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            if (string.IsNullOrEmpty(v)) return "Bansa";
            var plus = v.IndexOf('+');          // strip SourceLink build metadata if present
            if (plus >= 0) v = v[..plus];
            return $"Bansa v{v}";
        }
    }

    /// <summary>Just the "vX.Y.Z" portion (no "Bansa" prefix), for the title-bar label.</summary>
    public string AppVersionShort
    {
        get
        {
            var full = AppVersion;                 // "Bansa v0.9.0"
            var i = full.IndexOf('v');
            return i >= 0 ? full[i..] : full;      // "v0.9.0"
        }
    }

    public string TotalDownText  => Format.Rate(TotalDownBps);
    public string TotalUpText    => Format.Rate(TotalUpBps);
    public string TodayDownText  => _todayBytesIn  > 0 ? Format.Bytes(_todayBytesIn)  : "—";
    public string TodayUpText    => _todayBytesOut > 0 ? Format.Bytes(_todayBytesOut) : "—";

    /// <summary>
    /// User-defined label for the current ping target, or the raw target string if no label set.
    /// Shown in the sidebar ping card and tray popup header.
    /// </summary>
    public string PingDisplayLabel =>
        App.Settings.PingTargetLabels.TryGetValue(App.Settings.PingTarget, out var lbl) && !string.IsNullOrEmpty(lbl)
            ? lbl
            : App.Settings.PingTarget;

    /// <summary>Call after saving or clearing a ping label to push the updated text to the sidebar.</summary>
    public void NotifyPingDisplayLabel() => OnPropertyChanged(nameof(PingDisplayLabel));
    public string PingText => PingMs < 0 ? "— ms" : $"{PingMs} ms";
    public string ThresholdLabel => HideBelowKBps <= 0
        ? "Show all apps"
        : $"Hide apps below {HideBelowKBps:0.#} KB/s";

    public IReadOnlyList<(long Down, long Up)> History => _rateHistory.Snapshot();

    /// <summary>
    /// Returns per-tick app snapshots in the same chronological order as
    /// <see cref="History"/> (oldest first, newest last).  Each element is the
    /// top-5 active apps at that tick, or <c>null</c> when no apps were active.
    /// Must be called on the UI thread (written there, read there).
    /// </summary>
    public IReadOnlyList<(string Name, string ImagePath, long DownBps, long UpBps)[]?> AppTickSnapshot()
    {
        var result = new List<(string, string, long, long)[]?>(_appAtTickCount);
        int start = _appAtTickCount < kChartCapacity ? 0 : _appAtTickHead;
        for (int i = 0; i < _appAtTickCount; i++)
            result.Add(_appAtTick[(start + i) % kChartCapacity]);
        return result;
    }

    public event Action<long, long, int, IReadOnlyList<(long Down, long Up)>, IEnumerable<AppRowViewModel>>? TraySnapshot;

    public MainViewModel()
    {
        _downloadThrottler = new DownloadThrottler(_monitor);

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = AppFilter;
        // SortPriority is always the primary key (pinned=0, modified=1, normal=2).
        // Secondary sort defaults to highest download — user column-clicks add/replace secondary.
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.SortPriority), ListSortDirection.Ascending));
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.BytesInPerSec), ListSortDirection.Descending));

        // IsLiveSorting: when BytesInPerSec changes, the view moves the row in-place.
        // This sends CollectionChanged(Move) — cheap, no row containers are recreated.
        // IsLiveFiltering is intentionally OFF: WPF has a bug where items filtered-out
        // immediately on insertion (rate=0 first sample) are not re-evaluated when their
        // rate later rises.  We handle that edge case with a targeted Refresh() only when
        // an app transitions from zero-traffic to active (see OnSampleReady).
        var lv = (System.Windows.Data.ListCollectionView)AppsView;
        lv.IsLiveSorting = true;
        lv.LiveSortingProperties.Add(nameof(AppRowViewModel.SortPriority));
        lv.LiveSortingProperties.Add(nameof(AppRowViewModel.BytesInPerSec));
        lv.LiveSortingProperties.Add(nameof(AppRowViewModel.BytesOutPerSec));

        IsDarkTheme = ThemeManager.Current == AppTheme.Dark;
        HideBelowKBps = App.Settings.HideBelowKBps;
        UseBitsUnit = string.Equals(App.Settings.RateUnit, "Bits", StringComparison.OrdinalIgnoreCase);
        TrayIconSize = App.Settings.TrayIconSize;
        UseWindowsAccent = App.Settings.UseWindowsAccent;
        GlobalUploadCapKBs = App.Settings.GlobalUploadCapKBs;
        // Set the backing field directly so the OnChanged handler doesn't apply the cap during
        // construction — the startup block below owns the initial apply.
        isGlobalUploadCapEnabled = App.Settings.GlobalUploadCapEnabled;
        IsGlobalUploadCapPersistent = App.Settings.GlobalUploadCapPersist;
        IsScenarioActive = App.Settings.ScenarioActive;
        ShowFloatingGraph  = App.Settings.ShowFloatingGraph;
        HideLocalOnlyApps  = App.Settings.HideLocalOnlyApps;

        _ping = new PingMonitor(App.Settings.PingTarget);
        _ping.Updated += OnPingUpdated;
        _ping.Start();

        // Re-apply all saved OS rules immediately — don't wait for ETW to detect each process.
        // QoS and firewall rules work by filename, so they can be activated before the game starts.
        _ = Task.Run(EagerlyReapplySavedRulesAsync);

        _monitor.SampleReady += OnSampleReady;
        try
        {
            _monitor.Start();
            StatusText = "Monitoring active — per-app traffic via ETW.";
        }
        catch (Exception ex)
        {
            StatusText = "Monitor failed to start: " + ex.Message;
        }

        // Re-apply global upload cap from previous session (standalone — independent of Scenarios).
        if (App.Settings.GlobalUploadCapEnabled && App.Settings.GlobalUploadCapKBs > 0)
        {
            _ = Task.Run(async () =>
            {
                var problem = await QosManager.DiagnosePrerequisitesAsync();
                Application.Current?.Dispatcher.InvokeAsync(() => QosWarning = problem ?? "");
                _downloadThrottler.SetGlobalUploadCap(App.Settings.GlobalUploadCapKBs);
                try { await QosManager.SetGlobalUploadCapAsync(App.Settings.GlobalUploadCapKBs); }
                catch (Exception ex) { Log.Debug("Startup global upload cap (QoS)", ex); }
            });
        }

        // Re-apply Scenarios profile limits if active from previous session.
        if (App.Settings.ScenarioActive)
        {
            foreach (var kv in App.Settings.ScenarioProfiles)
            {
                if (kv.Value.UploadKBs   > 0) _downloadThrottler.SetUploadLimit(kv.Key,   kv.Value.UploadKBs);
                if (kv.Value.DownloadKBs > 0) _downloadThrottler.SetDownloadLimit(kv.Key, kv.Value.DownloadKBs);
            }
        }
    }

    partial void OnTotalDownBpsChanged(long value) => OnPropertyChanged(nameof(TotalDownText));
    partial void OnTotalUpBpsChanged(long value) => OnPropertyChanged(nameof(TotalUpText));
    partial void OnPingMsChanged(int value) => OnPropertyChanged(nameof(PingText));
    partial void OnFilterTextChanged(string value) => AppsView.Refresh();
    partial void OnHideBelowKBpsChanged(double value)
    {
        App.Settings.HideBelowKBps = value;
        SettingsManager.Save(App.Settings);
        OnPropertyChanged(nameof(ThresholdLabel));
        AppsView.Refresh();
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        ThemeManager.Apply(value ? AppTheme.Dark : AppTheme.Light);
        App.Settings.Theme = value ? "Dark" : "Light";
        SettingsManager.Save(App.Settings);
    }

    partial void OnUseBitsUnitChanged(bool value)
    {
        App.Settings.RateUnit = value ? "Bits" : "Bytes";
        SettingsManager.Save(App.Settings);
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        foreach (var a in Apps) a.RefreshUnits();
    }

    partial void OnTrayIconSizeChanged(int value)
    {
        App.Settings.TrayIconSize = value;
        SettingsManager.Save(App.Settings);
    }

    partial void OnGlobalUploadCapKBsChanged(int value)
    {
        App.Settings.GlobalUploadCapKBs = value;
        SettingsManager.Save(App.Settings);
    }

    partial void OnIsGlobalUploadCapPersistentChanged(bool value)
    {
        // Only affects exit behaviour (whether the QoS policy is left in place); nothing to
        // apply now — the cap is already active or not per the Enabled switch.
        App.Settings.GlobalUploadCapPersist = value;
        SettingsManager.Save(App.Settings);
    }

    partial void OnIsScenarioActiveChanged(bool value)
    {
        App.Settings.ScenarioActive = value;
        SettingsManager.Save(App.Settings);
    }

    partial void OnShowFloatingGraphChanged(bool value)
    {
        App.Settings.ShowFloatingGraph = value;
        SettingsManager.Save(App.Settings);
    }

    partial void OnHideLocalOnlyAppsChanged(bool value)
    {
        App.Settings.HideLocalOnlyApps = value;
        SettingsManager.Save(App.Settings);
        AppsView.Refresh();
    }

    partial void OnUseWindowsAccentChanged(bool value)
    {
        // Accent is now owned per-domain by DomainManager; this legacy setting no longer
        // drives the accent. Persist it and re-assert the domain accent.
        App.Settings.UseWindowsAccent = value;
        SettingsManager.Save(App.Settings);
        DomainManager.Apply(DomainManager.Current);
    }

    public void SetDownColor(string hex)
    {
        App.Settings.DownColorHex = hex;
        SettingsManager.Save(App.Settings);
        UpdateChartBrush("ChartDownBrush", hex);
    }

    public void SetUpColor(string hex)
    {
        App.Settings.UpColorHex = hex;
        SettingsManager.Save(App.Settings);
        UpdateChartBrush("ChartUpBrush", hex);
    }

    public void SetCpuColor(string hex)  { App.Settings.CpuColorHex = hex; SettingsManager.Save(App.Settings); UpdateChartBrush("ChartCpuBrush", hex); UpdateMutedBrush("ChartCpuMutedBrush", hex); }
    public void SetGpuColor(string hex)  { App.Settings.GpuColorHex = hex; SettingsManager.Save(App.Settings); UpdateChartBrush("ChartGpuBrush", hex); UpdateMutedBrush("ChartGpuMutedBrush", hex); }
    public void SetRamColor(string hex)  { App.Settings.RamColorHex = hex; SettingsManager.Save(App.Settings); UpdateChartBrush("ChartRamBrush", hex); UpdateMutedBrush("ChartRamMutedBrush", hex); }
    // Temperature bands (stepped). Each band has a flat color; thresholds saved separately.
    public void SetTempCoolBand(string hex) { App.Settings.TempBandCoolColorHex = hex; SettingsManager.Save(App.Settings); }
    public void SetTempWarmBand(string hex) { App.Settings.TempBandWarmColorHex = hex; SettingsManager.Save(App.Settings); }
    public void SetTempHotBand(string hex)  { App.Settings.TempBandHotColorHex  = hex; SettingsManager.Save(App.Settings); }
    public void SetPingGoodColor(string hex) { App.Settings.PingGoodColorHex = hex; SettingsManager.Save(App.Settings); }
    public void SetPingBadColor(string hex)  { App.Settings.PingBadColorHex  = hex; SettingsManager.Save(App.Settings); }

    private static void UpdateChartBrush(string resourceKey, string hex)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var brush = new System.Windows.Media.SolidColorBrush(c);
                if (brush.CanFreeze) brush.Freeze();
                Application.Current.Resources[resourceKey] = brush;
            }
            catch { }
        });
    }

    /// <summary>Updates a low-alpha tint resource (for icon tile backgrounds) from a hex color.</summary>
    private static void UpdateMutedBrush(string resourceKey, string hex, byte alpha = 0x20)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
                if (brush.CanFreeze) brush.Freeze();
                Application.Current.Resources[resourceKey] = brush;
            }
            catch { }
        });
    }

    private bool AppFilter(object obj)
    {
        if (obj is not AppRowViewModel a) return false;
        // Pinned / blocked / limited / prioritised apps are always visible regardless of filter or threshold
        if (a.SortPriority < 2) return true;
        if (!string.IsNullOrWhiteSpace(FilterText) && !a.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
            return false;
        if (HideLocalOnlyApps && a.IsLocalOnly)
            return false;
        double threshold = HideBelowKBps * 1024.0;
        if (threshold > 0 && a.BytesInPerSec < threshold && a.BytesOutPerSec < threshold)
            return false;
        return true;
    }

    private void OnSampleReady(IReadOnlyList<ProcessNetInfo> sample)
    {
        var groups = sample
            .GroupBy(p => string.IsNullOrEmpty(p.Name) ? $"pid-{p.Pid}" : p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            long sumIn = 0, sumOut = 0;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Batch all updates inside DeferRefresh so IsLiveSorting coalesces
            // all per-property moves into one sort pass at the end.
            using (((ListCollectionView)AppsView).DeferRefresh())
            {
                foreach (var g in groups)
                {
                    seenNames.Add(g.Key);

                    _rowsByName.TryGetValue(g.Key, out var existingRow);
                    bool isNew = existingRow is null;
                    AppRowViewModel row;
                    if (isNew)
                    {
                        row = new AppRowViewModel { Name = g.Key };
                        _rowsByName[g.Key] = row;
                        Apps.Add(row);
                        _needsFilterRefresh = true;   // structural add always needs a re-filter
                    }
                    else
                    {
                        row = existingRow!;
                    }

                    var children = g.Select(p => new ProcessRowViewModel
                    {
                        Pid = p.Pid,
                        Name = p.Name,
                        ImagePath = p.ImagePath,
                        BytesInPerSec = p.BytesInPerSec,
                        BytesOutPerSec = p.BytesOutPerSec,
                        TotalBytesIn = p.TotalBytesIn,
                        TotalBytesOut = p.TotalBytesOut,
                        ConnectionCount = p.Connections.Count,
                    }).ToList();

                    // Detect zero-traffic → active transition so the filter re-runs.
                    // An app starts with rate=0 (EMA warms up on second sample) and
                    // TotalBytesIn=0 (no packets yet), making it invisible when a
                    // KB/s threshold is set.  WPF's IsLiveFiltering won't re-evaluate
                    // items that were filtered-out immediately on insertion, so we track
                    // the transition here and do a targeted Refresh() after the block.
                    bool wasInvisible = !isNew
                        && row.BytesInPerSec  == 0 && row.BytesOutPerSec == 0
                        && row.TotalBytesIn   == 0 && row.TotalBytesOut  == 0;

                    row.UpdateFromChildren(children);

                    if (wasInvisible && (row.BytesInPerSec > 0 || row.TotalBytesIn > 0 || row.TotalBytesOut > 0))
                        _needsFilterRefresh = true;

                    // Download badge: poll the throttler so it turns amber while the firewall
                    // block is actually firing (≤500 ms lag).
                    if (row.DownloadLimitKbps > 0)
                        row.IsThrottlingDown = _downloadThrottler.IsActivelyThrottlingDown(row.ImagePath);
                    else if (row.IsThrottlingDown)
                        row.IsThrottlingDown = false;

                    // Upload badge: QoS gives no enforcement feedback, so derive effectiveness from
                    // the measured rate vs the limit (amber = exceeding cap = not enforced yet).
                    row.UpdateUploadCapEffectiveness();

                    // Classify local-only: all observed connections go to loopback / RFC-1918
                    var allConns = g.SelectMany(p => p.Connections).ToList();
                    row.IsLocalOnly = allConns.Count > 0
                        && allConns.All(c => IsLocalAddress(c.RemoteAddress));

                    // UDP detection: flag apps that have active external UDP sockets.
                    // QoS upload shaping classifies sockets at creation, so QUIC/UDP apps that reuse
                    // one long-lived connection can dodge the cap — SetLimitWindow warns about this.
                    row.HasUdpConnections = allConns.Any(c =>
                        c.Protocol == "UDP" && !IsLocalAddress(c.RemoteAddress));

                    // Restore saved limits the first time we see an app's image path
                    if (!string.IsNullOrEmpty(row.ImagePath) &&
                        _restoredLimitPaths.Add(row.ImagePath))
                    {
                        RestoreSavedLimits(row);
                    }

                    sumIn += row.BytesInPerSec;
                    sumOut += row.BytesOutPerSec;
                }

                var deadNames = _rowsByName.Keys.Where(k => !seenNames.Contains(k)).ToList();
                foreach (var name in deadNames)
                {
                    Apps.Remove(_rowsByName[name]);
                    _rowsByName.Remove(name);
                    _needsFilterRefresh = true;
                }
            } // DeferRefresh ends — IsLiveSorting applies queued row-moves in one pass

            // Only do a full Refresh() when filter visibility actually changed
            // (new app, app went active from zero, or app removed). This avoids the
            // CollectionChanged(Reset) that destroys and recreates all row containers.
            if (_needsFilterRefresh)
            {
                ((System.Windows.Data.ListCollectionView)AppsView).Refresh();
                _needsFilterRefresh = false;
            }

            TotalDownBps = sumIn;
            TotalUpBps = sumOut;
            _rateHistory.Push(sumIn, sumOut);

            // Capture top-5 visible apps for the crosshair tooltip history.
            // AppsView is already sorted by BytesInPerSec DESC, so Take(5) gives the right order.
            var topApps = AppsView.Cast<AppRowViewModel>()
                .Where(a => a.BytesInPerSec > 0 || a.BytesOutPerSec > 0)
                .Take(5)
                .Select(a => (a.Name, a.ImagePath, a.BytesInPerSec, a.BytesOutPerSec))
                .ToArray();
            _appAtTick[_appAtTickHead] = topApps.Length > 0 ? topApps : null;
            _appAtTickHead = (_appAtTickHead + 1) % kChartCapacity;
            if (_appAtTickCount < kChartCapacity) _appAtTickCount++;

            // Refresh today's usage from the history DB every ~10 s (20 ticks × 500 ms)
            _todayRefreshCounter++;
            if (_todayRefreshCounter % 20 == 0 || _todayRefreshCounter == 1)
                _ = RefreshTodayUsageAsync();

            // Filter hysteresis: when a speed threshold is active, re-evaluate the filter
            // every ~5 s so apps that slow down below the threshold eventually disappear,
            // without re-running the filter on every tick (which would cause visual flickering).
            if (HideBelowKBps > 0 && _todayRefreshCounter % 10 == 5)
                _needsFilterRefresh = true;

            try
            {
                // Pass the *filtered + sorted* view so tray popup and floating graph mirror
                // exactly what the main window shows (KB/s threshold, text filter, Hide local, etc.)
                TraySnapshot?.Invoke(sumIn, sumOut, PingMs, _rateHistory.Snapshot(),
                    AppsView.Cast<AppRowViewModel>().ToList());
            }
            catch { }

            // Refresh hourly usage for row tooltips every ~30 s (60 samples × 500 ms)
            _hourlyRefreshCounter++;
            if (_hourlyRefreshCounter % 60 == 0)
                _ = RefreshHourlyUsageAsync();
        });

        try { _history.RecordSample(sample); } catch { }

        if ((DateTime.UtcNow - _lastRollup).TotalMinutes > 30)
        {
            _lastRollup = DateTime.UtcNow;
            try { _history.Rollup(); } catch (Exception ex) { Log.Debug("History rollup", ex); }
        }
    }

    /// <summary>
    /// Re-apply every saved rule at startup without waiting for ETW.
    /// QoS/firewall rules are keyed by filename, so no running process is needed.
    /// </summary>
    private async Task EagerlyReapplySavedRulesAsync()
    {
        var s = App.Settings;

        foreach (var path in s.AppBlockedPaths.ToList())
            try { await FirewallManager.BlockAppAsync(path); }
            catch (Exception ex) { Log.Debug($"EagerlyReapply block '{path}'", ex); }

        // Upload limits = QoS smooth shaping (new connections); download limits = pulsed
        // inbound firewall. Both keyed by filename, so they re-arm before the process is seen.
        foreach (var kv in s.AppUploadLimitsKBs.ToList())
            if (kv.Value > 0) _downloadThrottler.SetUploadLimit(kv.Key, kv.Value);

        foreach (var kv in s.AppDownloadLimitsKBs.ToList())
            if (kv.Value > 0) _downloadThrottler.SetDownloadLimit(kv.Key, kv.Value);

    }

    private void RestoreSavedLimits(AppRowViewModel row)
    {
        var key = row.ImagePath.ToLowerInvariant();

        // Restore pinned state
        if (App.Settings.PinnedAppPaths.Any(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase)))
            row.IsPinned = true;

        // Re-apply firewall block (rules were removed on last exit)
        if (App.Settings.AppBlockedPaths.Contains(key))
        {
            row.IsBlocked = true;
            _ = FirewallManager.BlockAppAsync(row.ImagePath);
        }

        // Re-apply upload limit (smooth QoS shaping — applies on the app's next connection)
        if (App.Settings.AppUploadLimitsKBs.TryGetValue(key, out var upKBs) && upKBs > 0)
        {
            row.UploadLimitKbps = upKBs;
            _downloadThrottler.SetUploadLimit(row.ImagePath, upKBs);
        }

        // Re-apply download limit
        if (App.Settings.AppDownloadLimitsKBs.TryGetValue(key, out var downKBs) && downKBs > 0)
        {
            row.DownloadLimitKbps = downKBs;
            _downloadThrottler.SetDownloadLimit(row.ImagePath, downKBs);
        }

        // If Scenarios is active and this app has a profile, override the base limits now
        if (IsScenarioActive && App.Settings.ScenarioProfiles.TryGetValue(key, out var gm))
        {
            if (gm.UploadKBs > 0)
            {
                _downloadThrottler.SetUploadLimit(row.ImagePath, gm.UploadKBs);
                row.UploadLimitKbps = gm.UploadKBs;
            }
            if (gm.DownloadKBs > 0)
            {
                _downloadThrottler.SetDownloadLimit(row.ImagePath, gm.DownloadKBs);
                row.DownloadLimitKbps = gm.DownloadKBs;
            }
        }
    }

    // ── Global upload cap ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ApplyGlobalUploadCapAsync()
    {
        if (GlobalUploadCapKBs <= 0)
        {
            StatusText = "Enter an upload cap value (KB/s) first, then Apply.";
            return;
        }
        // "Apply" turns the cap on at the current value. If it's already on, push the edited value.
        if (!IsGlobalUploadCapEnabled)
            IsGlobalUploadCapEnabled = true;   // OnChanged applies the cap
        else
            await ApplyGlobalCapAsync(GlobalUploadCapKBs);
    }

    partial void OnIsGlobalUploadCapEnabledChanged(bool value)
    {
        App.Settings.GlobalUploadCapEnabled = value;
        SettingsManager.Save(App.Settings);
        _ = ApplyGlobalCapAsync(value ? GlobalUploadCapKBs : 0);
    }

    /// <summary>
    /// Applies (kbps &gt; 0) or removes (kbps = 0) the global upload cap across both enforcement
    /// layers. Shared by the Apply command, the enable/disable toggle, and Clear.
    /// </summary>
    private async Task ApplyGlobalCapAsync(int kbps)
    {
        // Check QoS health and surface the result in the Settings banner.
        // Do NOT block apply — the user may be troubleshooting or the diagnosis may be stale.
        if (kbps > 0)
        {
            var problem = await QosManager.DiagnosePrerequisitesAsync();
            QosWarning = problem ?? "";
        }
        else
        {
            QosWarning = "";
        }

        // Hard enforcement layer — pulsed firewall rules, catches existing connections and UDP
        _downloadThrottler.SetGlobalUploadCap(kbps);

        // Soft cap layer — QoS Group Policy, zero CPU overhead once applied
        var o = await QosManager.SetGlobalUploadCapAsync(kbps);
        StatusText = o.Success
            ? (kbps > 0
                ? $"Global upload cap applied: {Format.KBps(kbps)} — bufferbloat protection active."
                : "Global upload cap disabled.")
            : "Failed to apply global upload cap: " + o.Detail;
    }

    [RelayCommand]
    private async Task RefreshQosStatusAsync()
    {
        var problem = await QosManager.DiagnosePrerequisitesAsync();
        QosWarning = problem ?? "";
        StatusText = string.IsNullOrEmpty(QosWarning)
            ? "QoS Packet Scheduler OK — global upload cap will be enforced."
            : "QoS check: " + QosWarning;
    }

    // ── Scenarios ───────────────────────────────────────────────────────────

    [RelayCommand]
    private Task ToggleScenarioAsync()
    {
        IsScenarioActive = !IsScenarioActive;
        var profiles = App.Settings.ScenarioProfiles;

        if (IsScenarioActive)
        {
            foreach (var kv in profiles)
            {
                var row = FindRowByPath(kv.Key);
                if (kv.Value.UploadKBs > 0)
                {
                    _downloadThrottler.SetUploadLimit(kv.Key, kv.Value.UploadKBs);
                    if (row != null) row.UploadLimitKbps = kv.Value.UploadKBs;
                }
                if (kv.Value.DownloadKBs > 0)
                {
                    _downloadThrottler.SetDownloadLimit(kv.Key, kv.Value.DownloadKBs);
                    if (row != null) row.DownloadLimitKbps = kv.Value.DownloadKBs;
                }
            }
            int count = profiles.Count;
            StatusText = count > 0
                ? $"Scenarios ON — {count} app {(count == 1 ? "profile" : "profiles")} applied."
                : "Scenarios ON — no profiles configured. Add apps in Settings → Scenarios.";
        }
        else
        {
            foreach (var kv in profiles)
            {
                var row = FindRowByPath(kv.Key);
                // Restore per-app limits from persistent settings (0 = no limit)
                if (kv.Value.UploadKBs > 0)
                {
                    var baseUp = App.Settings.AppUploadLimitsKBs.TryGetValue(kv.Key, out var u) ? u : 0;
                    _downloadThrottler.SetUploadLimit(kv.Key, baseUp);
                    if (row != null) row.UploadLimitKbps = baseUp;
                }
                if (kv.Value.DownloadKBs > 0)
                {
                    var baseDown = App.Settings.AppDownloadLimitsKBs.TryGetValue(kv.Key, out var d) ? d : 0;
                    _downloadThrottler.SetDownloadLimit(kv.Key, baseDown);
                    if (row != null) row.DownloadLimitKbps = baseDown;
                }
            }
            StatusText = "Scenarios OFF — per-app limits restored.";
        }

        return Task.CompletedTask;
    }

    private AppRowViewModel? FindRowByPath(string path)
        => Apps.FirstOrDefault(a => string.Equals(a.ImagePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Apps Bansa knows (history of the last 30 days + live processes), each with a
    /// launchable path when one has been learned. Used by the Network Tools bind dropdown.
    /// Learns paths from running apps + saved limits into settings so they persist.
    /// </summary>
    public List<KnownApp> GetKnownApps()
    {
        var settings = App.Settings;

        // Learn paths from currently-running networked apps.
        foreach (var a in Apps)
        {
            if (string.IsNullOrEmpty(a.ImagePath)) continue;
            var key = System.IO.Path.GetFileNameWithoutExtension(a.ImagePath).ToLowerInvariant();
            if (key.Length > 0) settings.KnownAppPaths[key] = a.ImagePath;
        }
        // Seed from saved limit / pin paths too (still full paths).
        foreach (var p in settings.AppDownloadLimitsKBs.Keys
                     .Concat(settings.AppUploadLimitsKBs.Keys)
                     .Concat(settings.PinnedAppPaths))
        {
            if (string.IsNullOrEmpty(p)) continue;
            var key = System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
            if (key.Length > 0 && !settings.KnownAppPaths.ContainsKey(key)) settings.KnownAppPaths[key] = p;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in _history.GetTotals(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow))
                if (!string.IsNullOrWhiteSpace(t.Name)) names.Add(t.Name);
        }
        catch { /* history unavailable — fall back to live apps only */ }
        foreach (var a in Apps)
            if (!string.IsNullOrWhiteSpace(a.Name)) names.Add(a.Name);

        var list = names
            .Select(n =>
            {
                settings.KnownAppPaths.TryGetValue(n.ToLowerInvariant(), out var path);
                return new KnownApp(n, path);
            })
            .OrderByDescending(k => k.HasPath)
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SettingsManager.Save(settings);
        return list;
    }

    /// <summary>Remembers a user-located exe path for a process name, so the dropdown keeps it.</summary>
    public void LearnAppPath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        var key = System.IO.Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (key.Length == 0) return;
        App.Settings.KnownAppPaths[key] = exePath;
        SettingsManager.Save(App.Settings);
    }

    /// <summary>
    /// Removes Scenarios override for one path and restores its base limit from settings.
    /// Called when a profile entry is deleted while Scenarios is on.
    /// </summary>
    public void ClearScenarioAppLimits(string path)
    {
        var row = FindRowByPath(path);
        var baseUp   = App.Settings.AppUploadLimitsKBs.TryGetValue(path,   out var u) ? u : 0;
        var baseDown = App.Settings.AppDownloadLimitsKBs.TryGetValue(path, out var d) ? d : 0;
        _downloadThrottler.SetUploadLimit(path,   baseUp);
        _downloadThrottler.SetDownloadLimit(path, baseDown);
        if (row != null) { row.UploadLimitKbps = baseUp; row.DownloadLimitKbps = baseDown; }
    }

    /// <summary>
    /// Applies Scenarios profile limits live (called when settings are edited while mode is on).
    /// 0 in either direction = restore base limit for that direction.
    /// </summary>
    public void ApplyScenarioAppLimits(string path, ScenarioEntry entry)
    {
        var row = FindRowByPath(path);
        if (entry.UploadKBs > 0)
        {
            _downloadThrottler.SetUploadLimit(path, entry.UploadKBs);
            if (row != null) row.UploadLimitKbps = entry.UploadKBs;
        }
        else
        {
            var baseUp = App.Settings.AppUploadLimitsKBs.TryGetValue(path, out var u) ? u : 0;
            _downloadThrottler.SetUploadLimit(path, baseUp);
            if (row != null) row.UploadLimitKbps = baseUp;
        }
        if (entry.DownloadKBs > 0)
        {
            _downloadThrottler.SetDownloadLimit(path, entry.DownloadKBs);
            if (row != null) row.DownloadLimitKbps = entry.DownloadKBs;
        }
        else
        {
            var baseDown = App.Settings.AppDownloadLimitsKBs.TryGetValue(path, out var d) ? d : 0;
            _downloadThrottler.SetDownloadLimit(path, baseDown);
            if (row != null) row.DownloadLimitKbps = baseDown;
        }
    }

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    [RelayCommand]
    private async Task BlockAsync(AppRowViewModel? app)
    {
        if (app is null || string.IsNullOrEmpty(app.ImagePath)) return;
        StatusText = $"Blocking {app.Name}…";
        var ok = await FirewallManager.BlockAppAsync(app.ImagePath);
        app.IsBlocked = ok;
        if (ok)
        {
            App.Settings.AppBlockedPaths.Add(app.ImagePath.ToLowerInvariant());
            SettingsManager.Save(App.Settings);
            _history.LogActivity(app.Name, "blocked");
        }
        StatusText = ok ? $"Blocked {app.Name}." : $"Failed to block {app.Name}.";
    }

    [RelayCommand]
    private async Task UnblockAsync(AppRowViewModel? app)
    {
        if (app is null || string.IsNullOrEmpty(app.ImagePath)) return;
        StatusText = $"Unblocking {app.Name}…";
        var ok = await FirewallManager.UnblockAppAsync(app.ImagePath);
        if (ok)
        {
            app.IsBlocked = false;
            App.Settings.AppBlockedPaths.Remove(app.ImagePath.ToLowerInvariant());
            SettingsManager.Save(App.Settings);
            _history.LogActivity(app.Name, "unblocked");
        }
        StatusText = ok ? $"Unblocked {app.Name}." : $"Failed to unblock {app.Name}.";
    }

    // Removes a per-app limit from settings by exe FILENAME rather than full path. The QoS policy
    // and firewall rule both identify an app by filename, and a multi-process app's row path can
    // drift (the row uses the longest child path), so a path-exact Remove can miss the stored key
    // and the limit gets re-applied on the next launch. Filename match keeps "Clear" reliable and
    // also sweeps any drift-orphaned duplicate entries — same granularity as the firewall/QoS layers.
    private static void RemoveLimitByFile(Dictionary<string, int> dict, string imagePath)
    {
        var file = System.IO.Path.GetFileName(imagePath);
        if (string.IsNullOrEmpty(file)) return;
        foreach (var k in dict.Keys
                     .Where(k => string.Equals(System.IO.Path.GetFileName(k), file, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            dict.Remove(k);
    }

    [RelayCommand]
    private async Task ApplyLimitsAsync((AppRowViewModel app, int upKbps, int downKbps)? param)
    {
        if (param is null) return;
        var (app, up, down) = param.Value;
        if (string.IsNullOrEmpty(app.ImagePath))
        {
            StatusText = $"Can't set a limit on {app.Name} — Bansa can't see its executable path. (System / protected processes can't be limited.)";
            return;
        }

        var key          = app.ImagePath.ToLowerInvariant();
        bool clearingUp   = up   <= 0;
        bool clearingDown = down <= 0;

        if (clearingUp || clearingDown)
        {
            // Await removal and verify the firewall rule is actually gone before updating UI.
            bool verified = await _downloadThrottler.ClearAndVerifyAsync(
                app.ImagePath, clearDown: clearingDown, clearUp: clearingUp);

            if (clearingUp)   { app.UploadLimitKbps   = 0; RemoveLimitByFile(App.Settings.AppUploadLimitsKBs,   app.ImagePath); }
            if (clearingDown) { app.DownloadLimitKbps  = 0; RemoveLimitByFile(App.Settings.AppDownloadLimitsKBs, app.ImagePath); }

            // Apply any direction that still has a positive limit.
            if (!clearingUp && up > 0)
            {
                _downloadThrottler.SetUploadLimit(app.ImagePath, up);
                app.UploadLimitKbps = up;
                App.Settings.AppUploadLimitsKBs[key] = up;
            }
            if (!clearingDown && down > 0)
            {
                _downloadThrottler.SetDownloadLimit(app.ImagePath, down);
                app.DownloadLimitKbps = down;
                App.Settings.AppDownloadLimitsKBs[key] = down;
            }

            SettingsManager.Save(App.Settings);

            if (clearingUp && clearingDown)
            {
                _history.LogActivity(app.Name, "limit_cleared");
                StatusText = verified
                    ? $"Limits cleared: {app.Name}."
                    : $"Limits cleared for {app.Name} but a firewall rule may still be active — run Cleanup in Settings if throttling persists.";
            }
            else
            {
                _history.LogActivity(app.Name, "limit_set", $"Up {Format.KBps(up)}  Down {Format.KBps(down)}");
                StatusText = verified
                    ? $"Limits set: {app.Name}  ↑ {Format.KBps(up)}  ↓ {Format.KBps(down)}"
                    : $"Limits set: {app.Name}  ↑ {Format.KBps(up)}  ↓ {Format.KBps(down)}  — warning: an old firewall rule may still be active, run Cleanup if throttling persists.";
            }
        }
        else
        {
            // Upload: smooth QoS shaping (no connection drops). QoS classifies sockets at
            // creation, so an existing connection stays uncapped until it reconnects — the
            // row's IsUploadCapExceeded badge surfaces that to the user.
            _downloadThrottler.SetUploadLimit(app.ImagePath, up);
            app.UploadLimitKbps = up;
            App.Settings.AppUploadLimitsKBs[key] = up;

            _downloadThrottler.SetDownloadLimit(app.ImagePath, down);
            app.DownloadLimitKbps = down;
            App.Settings.AppDownloadLimitsKBs[key] = down;

            SettingsManager.Save(App.Settings);
            _history.LogActivity(app.Name, "limit_set", $"Up {Format.KBps(up)}  Down {Format.KBps(down)}");

            // Read back from throttler to confirm in-memory state matches the request.
            bool setOk = _downloadThrottler.GetDownloadLimit(app.ImagePath) == down
                      && _downloadThrottler.GetUploadLimit(app.ImagePath)   == up;
            StatusText = setOk
                ? $"Limits set: {app.Name}  ↑ {Format.KBps(up)}  ↓ {Format.KBps(down)}"
                : $"Warning: limits may not have applied for {app.Name} — run Cleanup in Settings if throttling persists.";
        }
    }

    /// <summary>
    /// Sets (or clears, when both are 0) a per-app limit addressed by executable path —
    /// used by the Limits &amp; Scenarios inline editor where the app may not be running.
    /// If the app IS running, defers to the normal row-based path so its UI badges update.
    /// </summary>
    public async Task SetLimitByPathAsync(string imagePath, int upKbps, int downKbps)
    {
        if (string.IsNullOrEmpty(imagePath)) return;

        var running = Apps.FirstOrDefault(a =>
            string.Equals(a.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
        if (running is not null)
        {
            await ApplyLimitsAsync((running, upKbps, downKbps));
            return;
        }

        var key = imagePath.ToLowerInvariant();
        bool clearUp = upKbps <= 0, clearDown = downKbps <= 0;

        if (clearUp || clearDown)
            await _downloadThrottler.ClearAndVerifyAsync(imagePath, clearDown: clearDown, clearUp: clearUp);

        if (!clearUp)  { _downloadThrottler.SetUploadLimit(imagePath, upKbps);     App.Settings.AppUploadLimitsKBs[key]   = upKbps; }
        else           RemoveLimitByFile(App.Settings.AppUploadLimitsKBs,   imagePath);
        if (!clearDown){ _downloadThrottler.SetDownloadLimit(imagePath, downKbps); App.Settings.AppDownloadLimitsKBs[key] = downKbps; }
        else           RemoveLimitByFile(App.Settings.AppDownloadLimitsKBs, imagePath);

        SettingsManager.Save(App.Settings);
        StatusText = clearUp && clearDown
            ? $"Limits cleared: {System.IO.Path.GetFileNameWithoutExtension(imagePath)}."
            : $"Limits set: {System.IO.Path.GetFileNameWithoutExtension(imagePath)}  ↑ {Format.KBps(upKbps)}  ↓ {Format.KBps(downKbps)}";
    }

    [RelayCommand]
    private void PinApp(AppRowViewModel? app)
    {
        if (app is null) return;
        app.IsPinned = true;
        if (!string.IsNullOrEmpty(app.ImagePath))
        {
            var key = app.ImagePath.ToLowerInvariant();
            if (!App.Settings.PinnedAppPaths.Any(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase)))
                App.Settings.PinnedAppPaths.Add(key);
            SettingsManager.Save(App.Settings);
        }
        StatusText = $"Pinned {app.Name} to top.";
    }

    [RelayCommand]
    private void UnpinApp(AppRowViewModel? app)
    {
        if (app is null) return;
        app.IsPinned = false;
        if (!string.IsNullOrEmpty(app.ImagePath))
        {
            var key = app.ImagePath.ToLowerInvariant();
            App.Settings.PinnedAppPaths.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
            SettingsManager.Save(App.Settings);
        }
        StatusText = $"Unpinned {app.Name}.";
    }

    [RelayCommand]
    private async Task RunCleanupAsync()
    {
        var confirm = Views.ConfirmDialog.Show(
            "Remove all Bansa changes?",
            "This removes ALL Bansa firewall rules and QoS policies from your system.\n\n" +
            "Your history database in %LocalAppData%\\Bansa\\ is preserved.",
            confirmText: "Remove everything",
            cancelText: "Cancel",
            danger: true);
        if (!confirm) return;

        StatusText = "Cleaning up…";
        var report = await CleanupManager.RunAsync(removeDataFolder: false);

        foreach (var a in Apps)
        {
            // Clear in-memory throttler state so InnerTick doesn't re-add block rules
            if (a.DownloadLimitKbps > 0) _downloadThrottler.SetDownloadLimit(a.ImagePath, 0);
            if (a.UploadLimitKbps   > 0) _downloadThrottler.SetUploadLimit(a.ImagePath, 0);
            a.IsBlocked         = false;
            a.UploadLimitKbps   = 0;
            a.DownloadLimitKbps = 0;
        }
        // Remove Scenarios profile limits if active
        if (IsScenarioActive)
        {
            IsScenarioActive = false;
            foreach (var kv in App.Settings.ScenarioProfiles)
            {
                if (kv.Value.UploadKBs   > 0) _downloadThrottler.SetUploadLimit(kv.Key,   0);
                if (kv.Value.DownloadKBs > 0) _downloadThrottler.SetDownloadLimit(kv.Key, 0);
            }
        }
        // Clear persisted rules so they aren't re-applied on next startup
        App.Settings.AppDownloadLimitsKBs.Clear();
        App.Settings.AppUploadLimitsKBs.Clear();
        App.Settings.AppBlockedPaths.Clear();
        SettingsManager.Save(App.Settings);

        StatusText = "Cleanup complete.";
        Views.ConfirmDialog.Show("Cleanup complete", report.ToString(), confirmText: "OK", cancelText: null);
    }

    // ── Local-address classification ──────────────────────────────────────────

    // Shared with NetworkMonitor's ETW packet filter (IpClassifier) so row
    // classification and traffic counting agree on what counts as local —
    // including CGNAT (Tailscale/ZeroTier) and multicast, which the old
    // string-prefix version here missed.
    private static bool IsLocalAddress(string addr) => IpClassifier.IsLocal(addr);

    // ── Ping management ──────────────────────────────────────────────────────

    private void OnPingUpdated(int ms, string status)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            PingMs = ms;
            PingStatus = status;
            OnPropertyChanged(nameof(PingText));
        });
    }

    /// <summary>Swap out the PingMonitor for a different target without restarting the app.</summary>
    public void ChangePingTarget(string newTarget)
    {
        if (string.IsNullOrWhiteSpace(newTarget) ||
            string.Equals(App.Settings.PingTarget, newTarget, StringComparison.OrdinalIgnoreCase))
            return;

        _ping.Updated -= OnPingUpdated;
        _ping.Dispose();

        App.Settings.PingTarget = newTarget;
        SettingsManager.Save(App.Settings);

        _ping = new PingMonitor(newTarget);
        _ping.Updated += OnPingUpdated;
        _ping.Start();

        // Sidebar label may have changed (different label for the new target)
        Application.Current?.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(PingDisplayLabel)));
    }

    // ── Hourly usage for DataGrid row tooltips ────────────────────────────────

    /// <summary>
    /// Queries last-hour totals for every visible app and updates their HourlyDown/UpText.
    /// Called every ~30 s from the UI-thread Dispatcher block, so await resumes on the UI thread.
    /// Also callable from MainWindow for an immediate on-demand refresh (e.g. first load).
    /// </summary>
    public async Task TriggerHourlyRefreshAsync() => await RefreshHourlyUsageAsync();

    private async Task RefreshTodayUsageAsync()
    {
        try
        {
            var (bytesIn, bytesOut) = await Task.Run(() => _history.GetTodayTotals());
            _todayBytesIn  = bytesIn;
            _todayBytesOut = bytesOut;
            OnPropertyChanged(nameof(TodayDownText));
            OnPropertyChanged(nameof(TodayUpText));
        }
        catch { /* non-fatal */ }
    }

    private async Task RefreshHourlyUsageAsync()
    {
        var from = DateTime.UtcNow.AddHours(-1);
        var to   = DateTime.UtcNow;

        try
        {
            // One grouped query for every app instead of one query per app.
            var totals = await Task.Run(() => _history.GetTotals(from, to));
            var byName = new Dictionary<string, (long In, long Out)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, bytesIn, bytesOut) in totals)
                byName[name] = (bytesIn, bytesOut);

            // Back on UI thread (SynchronizationContext captured by await)
            foreach (var app in Apps)
            {
                byName.TryGetValue(app.Name, out var t);
                app.HourlyDownText = t.In  > 0 ? Format.Bytes(t.In)  : "—";
                app.HourlyUpText   = t.Out > 0 ? Format.Bytes(t.Out) : "—";
            }
        }
        catch { /* non-fatal — tooltips just stay stale */ }
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _history.Dispose();
        _ping.Dispose();
        _downloadThrottler.Dispose();

        // ── Tear down all OS-level rules when Bansa exits ───────────────────────
        // Limits are only active while Bansa runs; we re-apply them on next startup.
        // Exception: if the user opted to keep the global cap active while closed, the
        // QoS soft-cap policy is left in place (re-asserted after the broad teardown).
        //
        // This MUST block until done: fire-and-forget let the process exit mid-cleanup,
        // leaving QoS policies (and firewall rules) behind. We run it via Task.Run so the
        // awaits inside don't capture the UI SynchronizationContext (which would deadlock a
        // .Wait() on this thread), and cap the wait so a hung COM/registry call can't wedge
        // shutdown — the process is exiting regardless.
        int preserveCap = App.Settings.GlobalUploadCapPersist
                          && App.Settings.GlobalUploadCapEnabled
                          && App.Settings.GlobalUploadCapKBs > 0
            ? App.Settings.GlobalUploadCapKBs
            : 0;
        try
        {
            Task.Run(() => CleanupManager.RunAsync(removeDataFolder: false, preserveGlobalCapKBs: preserveCap))
                .Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex) { Log.Debug("Exit cleanup (OS rule teardown)", ex); }
    }
}
