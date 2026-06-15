using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bansa.Models;
using Bansa.Services;
using Bansa.ViewModels;
using CommunityToolkit.Mvvm.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolid = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace Bansa.Views;

public partial class NetworkToolsView : UserControl
{
    private const string ForceBindIpSite = "https://r1ch.net/projects/forcebindip";

    private MainViewModel? _vm;
    private List<KnownApp> _knownApps = new();
    private System.ComponentModel.ICollectionView? _appView;
    private string? _selectedExePath;
    private string _activeAlias = "";
    private bool _scanRequested;

    private readonly ObservableCollection<BoundRowVm> _boundRows = new();
    private readonly ObservableCollection<IfaceRowVm> _ifaceRows = new();
    private readonly ObservableCollection<ScanRowVm> _scanRows = new();
    private readonly DispatcherTimer _liveTimer;

    private static readonly WpfBrush OkBrush    = Freeze("#4CAF50");
    private static readonly WpfBrush WarnBrush   = Freeze("#F59E0B");
    private static readonly WpfBrush InfoBrush   = Freeze("#9AA0A6");
    private static readonly WpfBrush ErrorBrush  = Freeze("#EF5350");

    public NetworkToolsView()
    {
        InitializeComponent();
        ActiveItems.ItemsSource = _boundRows;
        IfaceItems.ItemsSource  = _ifaceRows;
        ScanItems.ItemsSource   = _scanRows;

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => RefreshLive();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>Wires the shared MainViewModel (for the known-apps catalog).</summary>
    public void Init(MainViewModel vm) => _vm = vm;

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Refresh();
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
    }

    /// <summary>Full refresh — called on panel entry.</summary>
    public void Refresh()
    {
        LoadKnownApps();
        LoadAdapters();
        UpdateFbipStatus();
        ResetScan();           // the scan is on-demand — entering the panel clears any prior snapshot
        RefreshLive();
        RefreshInterfaces();   // refreshes the active-connection alias
    }

    // ════ App ↔ connection binding ═══════════════════════════════════════════

    private void LoadKnownApps()
    {
        _knownApps = _vm?.GetKnownApps() ?? new List<KnownApp>();
        _appView = System.Windows.Data.CollectionViewSource.GetDefaultView(_knownApps);
        AppCombo.ItemsSource = _appView;
        ApplyAppFilter(AppSearchBox.Text);
    }

    private void OnAppSearchChanged(object sender, TextChangedEventArgs e) => ApplyAppFilter(AppSearchBox.Text);

    private void ApplyAppFilter(string? text)
    {
        if (_appView is null) return;
        text = text?.Trim() ?? "";
        _appView.Filter = text.Length == 0
            ? null
            : o => o is KnownApp k && k.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadAdapters()
    {
        var adapters = NetworkAdapters.ListActiveIpv4();
        AdapterCombo.ItemsSource = adapters;
        if (AdapterCombo.SelectedIndex < 0 && adapters.Count > 0) AdapterCombo.SelectedIndex = 0;

        bool any = adapters.Count > 0;
        NoAdaptersText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        AdapterCombo.Visibility   = any ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void OnRefreshAdapters(object sender, RoutedEventArgs e) => LoadAdapters();

    private void UpdateFbipStatus()
    {
        bool installed = AnyForceBindIp();
        FbipStatusText.Text = installed ? "ForceBindIP ready" : "ForceBindIP not installed";
        FbipSetupCard.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnGetForceBindIp(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ForceBindIpSite) { UseShellExecute = true });

    private void OnOpenToolsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.ToolsFolder);
            Process.Start(new ProcessStartInfo(App.ToolsFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Debug("OpenToolsFolder", ex); }
    }

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AppCombo.SelectedItem is not KnownApp k) return;
        if (k.HasPath)
        {
            SetSelectedExe(k.Path!);
        }
        else
        {
            _selectedExePath = null;
            PickedPathText.Text = $"Not located — click Browse to find {k.Name}.";
            HideBindStatus();
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Choose the program to launch",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            _vm?.LearnAppPath(dlg.FileName);
            SetSelectedExe(dlg.FileName);
        }
    }

    /// <summary>Records the chosen exe and warns up front if it's a kind ForceBindIP can't bind.</summary>
    private void SetSelectedExe(string path)
    {
        _selectedExePath = path;
        PickedPathText.Text = path;
        var warn = AppBindWarning(path);
        if (warn is not null) ShowBindStatus(warn, WarnBrush);
        else HideBindStatus();
    }

    /// <summary>Non-null when ForceBindIP can't bind this app (Store/MSIX or Electron/Chromium).</summary>
    private static string? AppBindWarning(string path)
    {
        if (path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0)
            return "This is a Microsoft Store (MSIX) app — it runs in an app container and can't be launched or bound this way. Use Interface priority below to steer it instead.";
        if (IsElectronApp(path))
            return "This looks like an Electron/Chromium app (Claude, Discord, Slack, VS Code, browsers). ForceBindIP can't bind these — their networking runs in a separate child process and prefers IPv6. Use Interface priority below instead.";
        return null;
    }

    private static bool IsElectronApp(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;
        string[] markers = { "icudtl.dat", "v8_context_snapshot.bin", "chrome_100_percent.pak", @"resources\app.asar" };
        int hits = 0;
        foreach (var m in markers)
        {
            try { if (File.Exists(Path.Combine(dir, m))) hits++; } catch { }
        }
        return hits >= 2;   // two markers ⇒ confidently Chromium/Electron
    }

    // ── ForceBindIP discovery (bitness-aware) ─────────────────────────────────

    private static string? FindInTools(string name)
    {
        var dir = App.ToolsFolder;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static bool AnyForceBindIp() =>
        FindInTools("ForceBindIP64.exe") is not null || FindInTools("ForceBindIP.exe") is not null;

    /// <summary>
    /// Picks the ForceBindIP build matching the target's bitness — the #1 reason binding
    /// silently fails is using the 32-bit launcher on a 64-bit app (it injects nothing).
    /// </summary>
    private static (string? path, string? error) PickForceBindIp(string targetExe)
    {
        var fb64 = FindInTools("ForceBindIP64.exe");
        var fb32 = FindInTools("ForceBindIP.exe");
        if (fb64 is null && fb32 is null)
            return (null, "ForceBindIP not found in Data\\Tools\\. Use “Get ForceBindIP”.");

        return IsExe64Bit(targetExe) switch
        {
            true  => fb64 is not null ? (fb64, null)
                   : (null, "This is a 64-bit app — put ForceBindIP64.exe in Data\\Tools\\ (the 32-bit ForceBindIP.exe can't bind it)."),
            false => fb32 is not null ? (fb32, null)
                   : (null, "This is a 32-bit app — put the 32-bit ForceBindIP.exe in Data\\Tools\\."),
            _     => (fb64 ?? fb32, null),   // unknown bitness — prefer 64-bit
        };
    }

    private static bool? IsExe64Bit(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return null;        // 'MZ'
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null;    // 'PE\0\0'
            ushort machine = br.ReadUInt16();
            return machine switch
            {
                0x8664 => true,    // x64
                0xAA64 => true,    // ARM64
                0x014C => false,   // x86
                _      => (bool?)null,
            };
        }
        catch { return null; }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedExePath) || !File.Exists(_selectedExePath))
        {
            ShowBindStatus("Pick an app (and Browse to its .exe if it isn't located yet).", ErrorBrush);
            return;
        }
        if (AdapterCombo.SelectedItem is not NetAdapter adapter)
        {
            ShowBindStatus("Select a connection.", ErrorBrush);
            return;
        }

        var (forceBind, error) = PickForceBindIp(_selectedExePath);
        if (forceBind is null)
        {
            ShowBindStatus(error ?? "ForceBindIP not available.", ErrorBrush);
            return;
        }

        var extraArgs = ArgsBox.Text.Trim();
        var arguments = $"{adapter.Ipv4} \"{_selectedExePath}\"";
        if (extraArgs.Length > 0) arguments += " " + extraArgs;

        var preexisting = BindLaunchTracker.SnapshotPids(_selectedExePath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName         = forceBind,
                Arguments        = arguments,
                WorkingDirectory = Path.GetDirectoryName(forceBind)!,
                UseShellExecute  = false,
            });
        }
        catch (Exception ex)
        {
            ShowBindStatus($"Launch failed: {ex.Message}", ErrorBrush);
            return;
        }

        BindLaunchTracker.Register(_selectedExePath, adapter, preexisting);
        ShowBindStatus($"Launched {Path.GetFileName(_selectedExePath)} on {adapter.Kind} — watch the status below.", OkBrush);
        RefreshLive();
    }

    // ── Live bound-apps list ──────────────────────────────────────────────────

    // The bound-apps list updates live (timer); the scan is an explicit, on-demand snapshot.
    private void RefreshLive() => RefreshBound(ProcessEnumerator.GetConnectionsByPid());

    private void RefreshBound(Dictionary<int, List<ConnectionInfo>> conns)
    {
        _boundRows.Clear();
        foreach (var bl in BindLaunchTracker.Snapshot())
        {
            var state = BindLaunchTracker.Resolve(bl);
            if (state == BoundState.Exited) { BindLaunchTracker.Remove(bl); continue; }

            var status = BindLaunchTracker.Verify(bl, state, conns);
            var protoBrush = status.Proto.Contains("IPv6") ? WarnBrush : OkBrush;
            _boundRows.Add(new BoundRowVm(bl, status, BrushFor(status.Severity), protoBrush, () => RefreshLive()));
        }

        bool any = _boundRows.Count > 0;
        ActiveEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ActiveItems.Visibility = any ? Visibility.Visible   : Visibility.Collapsed;
    }

    private static WpfBrush BrushFor(BindSeverity s) => s switch
    {
        BindSeverity.Ok   => OkBrush,
        BindSeverity.Warn => WarnBrush,
        _                 => InfoBrush,
    };

    private void ShowBindStatus(string msg, WpfBrush brush)
    {
        BindStatusText.Text = msg;
        BindStatusText.Foreground = brush;
        BindStatusText.Visibility = Visibility.Visible;
    }

    private void HideBindStatus() => BindStatusText.Visibility = Visibility.Collapsed;

    // ════ Interface priority ══════════════════════════════════════════════════

    private async void RefreshInterfaces()
    {
        IfaceEmpty.Text = "Reading interfaces…";
        IfaceEmpty.Visibility = Visibility.Visible;
        IfaceActiveText.Visibility = Visibility.Collapsed;

        var ifaces      = await Task.Run(InterfacePriority.Read);
        var activeAlias = await Task.Run(InterfacePriority.ActiveInternetAlias);
        _activeAlias = activeAlias;

        var kinds = NetworkAdapters.ListActiveIpv4()
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.OrdinalIgnoreCase);

        int bestMetric = ifaces.Count > 0 ? ifaces.Min(m => m.EffectiveMetric) : int.MaxValue;
        var preferredAlias = App.Settings.PreferredAdapterAlias;
        bool hasExplicitPref = !string.IsNullOrEmpty(preferredAlias)
            && ifaces.Any(m => string.Equals(m.Alias, preferredAlias, StringComparison.OrdinalIgnoreCase));

        _ifaceRows.Clear();
        string? activeLabel = null;
        foreach (var m in ifaces)
        {
            var kind = kinds.TryGetValue(m.Alias, out var k) ? k : "Other";
            // Authoritative: the interface the OS actually uses to reach the internet.
            bool isActiveDefault = activeAlias.Length > 0
                ? string.Equals(m.Alias, activeAlias, StringComparison.OrdinalIgnoreCase)
                : m.EffectiveMetric == bestMetric;
            bool isPreferred = hasExplicitPref
                && string.Equals(m.Alias, preferredAlias, StringComparison.OrdinalIgnoreCase);
            if (isActiveDefault && activeLabel is null)
                activeLabel = kind == "Other" ? m.Alias : $"{kind} ({m.Alias})";
            _ifaceRows.Add(new IfaceRowVm(m, kind, isActiveDefault, isPreferred, hasExplicitPref, PreferAdapter));
        }

        bool any = _ifaceRows.Count > 0;
        IfaceEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) IfaceEmpty.Text = "No connected interfaces found.";

        if (activeLabel is not null)
        {
            IfaceActiveText.Text = $"Windows is currently routing through: {activeLabel}";
            IfaceActiveText.Visibility = Visibility.Visible;
        }

        RestorePrevBtn.IsEnabled = App.Settings.InterfacePriorityBackup.Count > 0;

        RefreshLive();   // bound apps
        // Keep an existing scan accurate against the now-current active connection.
        if (_scanRequested)
        {
            UpdateScanActiveText();
            RefreshScan(ProcessEnumerator.GetConnectionsByPid());
        }
    }

    private async void PreferAdapter(string alias)
    {
        SetIfaceBusy(true, $"Preferring {alias}…");
        await Task.Run(() => InterfacePriority.SetPreferred(alias, App.Settings));
        SettingsManager.Save(App.Settings);
        SetIfaceBusy(false, $"{alias} is now preferred.");
        RefreshInterfaces();
    }

    private async void OnRevertAutomatic(object sender, RoutedEventArgs e)
    {
        SetIfaceBusy(true, "Reverting to automatic…");
        await Task.Run(() => InterfacePriority.RevertToAutomatic(App.Settings));
        SettingsManager.Save(App.Settings);
        SetIfaceBusy(false, "All interfaces set back to automatic.");
        RefreshInterfaces();
    }

    private async void OnRestorePrevious(object sender, RoutedEventArgs e)
    {
        if (App.Settings.InterfacePriorityBackup.Count == 0)
        {
            IfaceStatusText.Text = "Nothing to restore.";
            return;
        }
        SetIfaceBusy(true, "Restoring previous metrics…");
        await Task.Run(() => InterfacePriority.RestorePrevious(App.Settings));
        SettingsManager.Save(App.Settings);
        SetIfaceBusy(false, "Restored to the metrics from before your change.");
        RefreshInterfaces();
    }

    private void SetIfaceBusy(bool busy, string status)
    {
        RevertAutoBtn.IsEnabled  = !busy;
        RestorePrevBtn.IsEnabled = !busy && App.Settings.InterfacePriorityBackup.Count > 0;
        IfaceStatusText.Text = status;
    }

    // ════ Connection scan ══════════════════════════════════════════════════════

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false;
        ScanEmpty.Text = "Scanning…";
        ScanEmpty.Visibility = Visibility.Visible;
        ScanItems.Visibility = Visibility.Collapsed;

        _activeAlias = await Task.Run(InterfacePriority.ActiveInternetAlias);
        _scanRequested = true;
        UpdateScanActiveText();
        RefreshScan(ProcessEnumerator.GetConnectionsByPid());

        ScanBtn.IsEnabled = true;
    }

    // Re-filter only if a scan was already taken — never trigger one implicitly.
    private void OnMismatchFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_scanRequested) RefreshScan(ProcessEnumerator.GetConnectionsByPid());
    }

    private void ResetScan()
    {
        _scanRequested = false;
        _scanRows.Clear();
        ScanActiveText.Visibility = Visibility.Collapsed;
        ScanItems.Visibility = Visibility.Collapsed;
        ScanEmpty.Visibility = Visibility.Visible;
        ScanEmpty.Text = "Click “Scan now” to list running apps and the connection each is using.";
    }

    private void UpdateScanActiveText()
    {
        if (_activeAlias.Length > 0)
        {
            ScanActiveText.Text = $"Active connection (internet traffic): {_activeAlias}";
            ScanActiveText.Visibility = Visibility.Visible;
        }
        else
        {
            ScanActiveText.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshScan(Dictionary<int, List<ConnectionInfo>> conns)
    {
        var ipMap = new Dictionary<string, NetAdapter>();
        foreach (var a in NetworkAdapters.ListActiveIpv4()) ipMap[a.Ipv4.ToString()] = a;

        var active = _activeAlias;
        bool onlyMismatch = OnlyMismatchCheck.IsChecked == true;

        var groups = new Dictionary<string, ScanAgg>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, list) in conns)
        {
            if (pid <= 4) continue;

            var kinds = new HashSet<string>();
            bool onActive = false, offActive = false, usedV6 = false, any = false;

            foreach (var c in list)
            {
                if (c.Protocol is "TCP6" or "UDP6")
                {
                    if (c.Protocol == "TCP6" && c.State == "ESTABLISHED" && RoutableV6(c.RemoteAddress))
                    { usedV6 = true; any = true; }
                    continue;
                }
                if (c.Protocol == "TCP" && c.RemoteAddress is "0.0.0.0" or "") continue;
                if (!IPAddress.TryParse(c.LocalAddress, out var lip) || IgnorableV4(lip)) continue;

                if (ipMap.TryGetValue(c.LocalAddress, out var ad))
                {
                    kinds.Add(ad.Kind);
                    if (string.Equals(ad.Name, active, StringComparison.OrdinalIgnoreCase)) onActive = true;
                    else offActive = true;
                    any = true;
                }
            }
            if (!any) continue;

            var (name, path) = ProcessEnumerator.GetProcessInfo(pid);
            if (!groups.TryGetValue(name, out var g))
            {
                g = new ScanAgg { Name = name, Path = string.IsNullOrEmpty(path) ? null : path };
                groups[name] = g;
            }
            foreach (var k in kinds) g.Kinds.Add(k);
            g.OnActive  |= onActive;
            g.OffActive |= offActive;
            g.UsedV6    |= usedV6;
            if (g.Path is null && !string.IsNullOrEmpty(path)) g.Path = path;
        }

        var rows = new List<ScanRowVm>();
        foreach (var g in groups.Values)
        {
            bool mismatch = active.Length > 0 && (g.OffActive || g.UsedV6);
            if (onlyMismatch && !mismatch) continue;

            var parts = g.Kinds.ToList();
            if (g.UsedV6) parts.Add("IPv6");
            var ifaceText = parts.Count > 0 ? string.Join(" + ", parts) : "—";

            var statusText = active.Length == 0 ? "Active connection unknown"
                           : mismatch ? "Not on the active connection"
                           : "On the active connection";
            var brush = active.Length == 0 ? InfoBrush : mismatch ? WarnBrush : OkBrush;

            bool canBind = mismatch && g.Path is not null;
            var pathCopy = g.Path;
            rows.Add(new ScanRowVm(g.Name, ifaceText, statusText, brush, canBind, mismatch,
                () => { if (pathCopy is not null) BindFromScan(pathCopy); }));
        }

        _scanRows.Clear();
        foreach (var r in rows.OrderByDescending(r => r.IsMismatch).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            _scanRows.Add(r);

        bool anyRows = _scanRows.Count > 0;
        ScanEmpty.Visibility = anyRows ? Visibility.Collapsed : Visibility.Visible;
        ScanItems.Visibility = anyRows ? Visibility.Visible   : Visibility.Collapsed;
        ScanEmpty.Text = onlyMismatch
            ? "No apps are off the active connection."
            : "No apps with active connections.";
    }

    private void BindFromScan(string path)
    {
        _vm?.LearnAppPath(path);
        SetSelectedExe(path);   // also warns if it's a Store/Electron app

        if (AdapterCombo.ItemsSource is IEnumerable<NetAdapter> ads)
        {
            var target = ads.FirstOrDefault(a => string.Equals(a.Name, _activeAlias, StringComparison.OrdinalIgnoreCase));
            if (target is not null) AdapterCombo.SelectedItem = target;
        }

        RootScroller.ScrollToTop();
        if (AppBindWarning(path) is null)
            ShowBindStatus(
                $"Ready to bind {Path.GetFileName(path)} to {_activeAlias}. Close the running copy first, then press “Launch bound”.",
                InfoBrush);
    }

    private static bool IgnorableV4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254);
    }

    private static bool RoutableV6(string a)
        => a.Length > 0 && a != "::" && a != "::1"
           && !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)
           && !a.StartsWith("ff", StringComparison.OrdinalIgnoreCase);

    private static WpfBrush Freeze(string hex)
    {
        var b = new WpfSolid((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private sealed class ScanAgg
    {
        public string Name = "";
        public string? Path;
        public readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase);
        public bool OnActive;
        public bool OffActive;
        public bool UsedV6;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Row view-models
// ════════════════════════════════════════════════════════════════════════════

public sealed class BoundRowVm
{
    public string   Name        { get; }
    public string   SubText     { get; }
    public string   StatusText  { get; }
    public WpfBrush StatusBrush { get; }
    public string   ProtoText   { get; }
    public Visibility ProtoVisibility { get; }
    public WpfBrush ProtoBrush  { get; }
    public RelayCommand StopCommand { get; }

    public BoundRowVm(BoundLaunch bl, BindStatus status, WpfBrush statusBrush, WpfBrush protoBrush, Action onChanged)
    {
        Name        = Path.GetFileName(bl.ExePath);
        SubText     = $"{bl.AdapterKind} · {bl.AdapterName} · {bl.BoundIp}";
        StatusText  = status.Text;
        StatusBrush = statusBrush;
        ProtoText   = status.Proto;
        ProtoVisibility = string.IsNullOrEmpty(status.Proto) ? Visibility.Collapsed : Visibility.Visible;
        ProtoBrush  = protoBrush;
        StopCommand = new RelayCommand(() => { BindLaunchTracker.Stop(bl); onChanged(); });
    }
}

public sealed class IfaceRowVm
{
    public string Title  { get; }
    public string Detail { get; }
    public string BadgeText { get; }
    public Visibility BadgeVisibility { get; }
    public string PreferLabel { get; }
    public bool CanPrefer { get; }
    public RelayCommand PreferCommand { get; }

    public IfaceRowVm(IfaceMetric m, string kind, bool isActiveDefault, bool isPreferred,
                      bool hasExplicitPref, Action<string> onPrefer)
    {
        Title  = kind == "Other" ? m.Alias : kind;
        var auto = m.V4Auto ? "automatic" : "manual";
        Detail = kind == "Other"
            ? $"IPv4 metric {m.V4Metric} ({auto})"
            : $"{m.Alias} · IPv4 metric {m.V4Metric} ({auto})";

        BadgeText = isPreferred ? "preferred"
                  : (!hasExplicitPref && isActiveDefault) ? "active"
                  : "";
        BadgeVisibility = BadgeText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreferLabel = isPreferred ? "Preferred ✓" : "Make preferred";
        CanPrefer = !isPreferred;
        PreferCommand = new RelayCommand(() => onPrefer(m.Alias));
    }
}

public sealed class ScanRowVm
{
    public string Name           { get; }
    public string InterfacesText { get; }
    public string StatusText     { get; }
    public WpfBrush StatusBrush   { get; }
    public bool CanBind          { get; }
    public Visibility BindVisibility { get; }
    public bool IsMismatch       { get; }
    public RelayCommand BindCommand { get; }

    public ScanRowVm(string name, string interfacesText, string statusText, WpfBrush brush,
                     bool canBind, bool isMismatch, Action onBind)
    {
        Name           = name;
        InterfacesText = interfacesText;
        StatusText     = statusText;
        StatusBrush    = brush;
        CanBind        = canBind;
        BindVisibility = canBind ? Visibility.Visible : Visibility.Collapsed;
        IsMismatch     = isMismatch;
        BindCommand    = new RelayCommand(onBind);
    }
}
