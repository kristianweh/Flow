using System;
using System.IO;
using System.Text.Json;

namespace Bansa.Services;

public enum RateUnit { Bytes, Bits }

/// <summary>Per-app limit applied when Scenarios is active.</summary>
public class ScenarioEntry
{
    public string AppName    { get; set; } = "";
    public int    UploadKBs  { get; set; } = 0;
    public int    DownloadKBs{ get; set; } = 0;
}

/// <summary>A named bandwidth-limit preset that can be applied from the limit window.</summary>
public class LimitProfile
{
    public string Name        { get; set; } = "";
    public int    UploadKbps  { get; set; } = 0;
    public int    DownloadKbps{ get; set; } = 0;
}

/// <summary>An interface's IPv4/IPv6 metric state, captured before a priority change so it can be restored.</summary>
public class InterfaceMetricBackup
{
    public bool AutomaticV4 { get; set; } = true;
    public int  MetricV4    { get; set; } = 0;
    public bool AutomaticV6 { get; set; } = true;
    public int  MetricV6    { get; set; } = 0;
}

public class BansaSettings
{
    public string Theme { get; set; } = "Dark";          // "Dark" or "Light"
    public string Domain { get; set; } = "Network";      // "Network" or "Hardware" — header domain mode
    public string NetworkColorHex  { get; set; } = "#00C8F0";  // Network domain dominant accent (dark-tuned base)
    public string HardwareColorHex { get; set; } = "#FF8A3D";  // Hardware domain dominant accent (thermal base)
    public double HideBelowKBps { get; set; } = 0;
    public bool HideLocalOnlyApps { get; set; } = false;  // hide apps whose all connections are loopback/LAN
    public bool MinimizeOnClose      { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;
    public bool ShowTrayIcon         { get; set; } = true;
    public bool WelcomeDismissed     { get; set; } = false;  // first-run reversibility explainer shown
    public bool HardwareHintDismissed { get; set; } = false; // one-time session-recording hint on the Hardware tab
    public string PingTarget { get; set; } = "8.8.8.8";
    public List<string> PingTargets { get; set; } = new()
        { "8.8.8.8", "1.1.1.1", "9.9.9.9", "8.8.4.4", "208.67.222.222" };
    public string RateUnit { get; set; } = "Bytes";      // "Bytes" or "Bits"
    public int TrayIconSize { get; set; } = 96;          // px; rendered at this resolution and Windows downscales
    public bool UseWindowsAccent { get; set; } = true;   // chart + accent follow OS accent
    public string DownColorHex { get; set; } = "#5DADE2";
    public string UpColorHex   { get; set; } = "#F39C12";
    // Hardware monitor chip colors (independent from network graph)
    public string CpuColorHex { get; set; } = "#5DADE2";
    public string GpuColorHex { get; set; } = "#FF8832";
    public string RamColorHex { get; set; } = "#10B981";
    // Temperature gradient endpoints (cold → hot) — legacy, kept for JSON back-compat (unused).
    public string TempColdColorHex { get; set; } = "#70C8FF";
    public string TempHotColorHex  { get; set; } = "#FF8080";
    // ── Temperature color bands (stepped — no blending) ───────────────────────
    // Two thresholds split the range into 3 bands; each band has its own flat color.
    // cool: < warm°C · warm: warm…hot°C · hot: ≥ hot°C
    public int    TempWarmThresholdC   { get; set; } = 60;
    public int    TempHotThresholdC    { get; set; } = 80;
    public string TempBandCoolColorHex { get; set; } = "#36BFFA";
    public string TempBandWarmColorHex { get; set; } = "#FFD60A";
    public string TempBandHotColorHex  { get; set; } = "#FF3B30";
    // Ping gradient endpoints (good → bad)
    public string PingGoodColorHex { get; set; } = "#10B981";
    public string PingBadColorHex  { get; set; } = "#F43F5E";

    // ── Global upload cap ────────────────────────────────────────────────────
    // For users without router-level QoS: system-wide upload ceiling enforced via
    // QoS Group Policy + pulsed firewall rules. 0 = disabled.
    public int GlobalUploadCapKBs { get; set; } = 0;
    // Master switch — lets the user toggle the cap off without losing the configured
    // KB/s value. The cap is active only when Enabled AND GlobalUploadCapKBs > 0.
    // Defaults true so a previously-saved cap value stays active across the upgrade.
    public bool GlobalUploadCapEnabled { get; set; } = true;
    // When true, the QoS soft-cap policy is left in place on exit so the cap keeps working
    // (and applies at Windows startup) without Bansa running. Deliberate exception to the
    // "tear everything down on exit" invariant — removed by Disable, the Clean Up button,
    // or Uninstall-Bansa.ps1. The firewall hard layer still requires the app to be running.
    public bool GlobalUploadCapPersist { get; set; } = false;

    // ── Scenarios (formerly "Gaming Mode") ────────────────────────────────────
    // When true, per-app limits from ScenarioProfiles are applied.
    // JSON keys retain the old "GamingMode*" names so settings saved before the
    // rename keep loading — do not change the JsonPropertyName values.
    [System.Text.Json.Serialization.JsonPropertyName("GamingModeActive")]
    public bool ScenarioActive  { get; set; } = false;
    // Key = exe path (lowercased). Applied on activation, removed on deactivation.
    [System.Text.Json.Serialization.JsonPropertyName("GamingModeProfiles")]
    public Dictionary<string, ScenarioEntry> ScenarioProfiles { get; set; } = new();

    // ── Ping target labels ───────────────────────────────────────────────────
    // Optional display name for each ping target.  Key = IP/hostname (case-insensitive).
    public Dictionary<string, string> PingTargetLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8.8.8.8"]          = "Google DNS",
        ["1.1.1.1"]          = "Cloudflare",
        ["9.9.9.9"]          = "Quad9",
        ["8.8.4.4"]          = "Google DNS 2",
        ["208.67.222.222"]   = "OpenDNS",
    };

    // ── Per-app limit persistence ─────────────────────────────────────────────
    // Keyed by image path (lowercased). Restored on startup when the app is detected.
    // All OS rules (QoS, firewall) are torn down on exit and re-applied on startup,
    // so limits are only active while Bansa is running.
    public Dictionary<string, int> AppUploadLimitsKBs   { get; set; } = new();
    public Dictionary<string, int> AppDownloadLimitsKBs { get; set; } = new();
    // Blocked app image paths (lowercased). Firewall rules re-created on startup.
    public HashSet<string> AppBlockedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Paths of apps pinned to the top of the process list (lowercased).
    public List<string> PinnedAppPaths { get; set; } = new();

    // ── Network Tools: per-app interface binding (ForceBindIP) ────────────────
    // Learned map of process name (lower-case, no extension) → last-seen full exe path,
    // so the bind dropdown can launch apps Bansa knows from history without re-browsing.
    public Dictionary<string, string> KnownAppPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Network Tools: interface priority ─────────────────────────────────────
    // Alias of the adapter currently forced to the front of the default route (""=none).
    public string PreferredAdapterAlias { get; set; } = "";
    // Pre-change metric state captured the first time the user sets a preference,
    // so "restore previous" can put every interface back exactly as it was.
    public Dictionary<string, InterfaceMetricBackup> InterfacePriorityBackup { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Global hotkey ─────────────────────────────────────────────────────────
    // Virtual-key code for the Ctrl+Shift+? hotkey. 0x46='F' default. 0 = no hotkey (cleared by user).
    public int HotkeyVirtualKey { get; set; } = 0x46;

    // ── Hardware monitor ──────────────────────────────────────────────────────
    /// <summary>GPU to display on multi-GPU machines (matched by LHM hardware name).
    /// Empty = automatic (NVIDIA dGPU &gt; AMD &gt; Intel).</summary>
    public string PreferredGpuName { get; set; } = "";

    // ── Monthly data budget ───────────────────────────────────────────────────
    /// <summary>Monthly usage budget in GB (download + upload combined). 0 = no budget.
    /// Shown against the "This month" tile in History (Network).</summary>
    public int MonthlyQuotaGB { get; set; } = 0;

    // ── Connection speed (used for limit suggestions) ─────────────────────────
    /// <summary>User's ISP upload speed in Mbps. 0 = not configured.</summary>
    public int ConnectionUploadMbps   { get; set; } = 0;
    /// <summary>User's ISP download speed in Mbps. 0 = not configured.</summary>
    public int ConnectionDownloadMbps { get; set; } = 0;
    /// <summary>When true, the Settings Network tab shows connection speed in Gbps.</summary>
    public bool ConnectionSpeedUnitGbps { get; set; } = false;

    // ── Named limit profiles ──────────────────────────────────────────────────
    public List<LimitProfile> LimitProfiles { get; set; } = new();

    // ── App grid sort + column persistence ───────────────────────────────────
    public string AppSortMemberPath  { get; set; } = "BytesInPerSec";
    public bool   AppSortDescending  { get; set; } = true;
    // Column widths keyed by header text; hidden column headers stored in the set.
    public Dictionary<string, double> AppGridColumnWidths  { get; set; } = new();
    public HashSet<string>            AppGridHiddenColumns { get; set; } = new(StringComparer.Ordinal);

    // ── Floating graph window ─────────────────────────────────────────────────
    public bool   ShowFloatingGraph  { get; set; } = false;
    public bool   ShowHardwarePanel  { get; set; } = true;    // SYS panel in float graph (legacy)
    public string FloatViewMode      { get; set; } = "Net";   // float graph tab: "Net" | "Temp" | "Both"
    public double FloatGraphX        { get; set; } = -1;      // -1 = auto-position
    public double FloatGraphY        { get; set; } = -1;
    public double FloatGraphW        { get; set; } = 340;
    public double FloatGraphH        { get; set; } = 240;      // slightly taller default for apps
    public bool   FloatGraphTopmost  { get; set; } = true;

    // ── Network chart ─────────────────────────────────────────────────────────
    /// <summary>When true, download and upload use independent Y-axis scales.</summary>
    public bool DualScale { get; set; } = false;

    // ── Tray hover popup ──────────────────────────────────────────────────────
    /// <summary>When true, the tray hover popup passes mouse events to windows underneath.</summary>
    public bool TrayClickThrough { get; set; } = false;
    /// <summary>When true, the tray hover popup stays above other windows (pin toggle).</summary>
    public bool TrayAlwaysOnTop { get; set; } = true;
    /// <summary>Tray hover popup opacity (0.3–1.0). 1.0 = fully opaque.</summary>
    public double TrayPopupOpacity { get; set; } = 1.0;

    // ── Main window bounds ────────────────────────────────────────────────────
    public double MainWindowX         { get; set; } = -1;
    public double MainWindowY         { get; set; } = -1;
    public double MainWindowW         { get; set; } = -1;
    public double MainWindowH         { get; set; } = -1;
    public bool   MainWindowMaximized { get; set; } = false;

    // ── Tray popup position ───────────────────────────────────────────────────
    public double TrayPopupX { get; set; } = -1;
    public double TrayPopupY { get; set; } = -1;

    // ── Network tab chart height (user-draggable via GridSplitter) ────────────
    public double NetworkChartHeight { get; set; } = 130;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(App.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static event Action? Changed;

    private static readonly (string Ip, string Label)[] _defaultTargets =
    {
        ("8.8.8.8",        "Google DNS"),
        ("1.1.1.1",        "Cloudflare"),
        ("9.9.9.9",        "Quad9"),
        ("8.8.4.4",        "Google DNS 2"),
        ("208.67.222.222", "OpenDNS"),
    };

    public static BansaSettings Load()
    {
        // A corrupt settings.json (crash/power loss mid-write) silently resetting every
        // limit and preference is the worst failure mode here, so fall back to the .bak
        // written by Save() before giving up and starting fresh.
        BansaSettings? s = TryLoadFile(SettingsPath);
        if (s is null && File.Exists(SettingsPath + ".bak"))
        {
            s = TryLoadFile(SettingsPath + ".bak");
            if (s is not null) Log.Debug("Settings: main file unreadable, restored from .bak");
        }
        s ??= new BansaSettings();

        // Merge default ping targets / labels for users upgrading from older versions.
        foreach (var (ip, label) in _defaultTargets)
        {
            if (!s.PingTargets.Contains(ip, StringComparer.OrdinalIgnoreCase))
                s.PingTargets.Add(ip);
            if (!s.PingTargetLabels.ContainsKey(ip))
                s.PingTargetLabels[ip] = label;
        }

        return s;
    }

    private static BansaSettings? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BansaSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) { Log.Debug($"Settings load '{Path.GetFileName(path)}'", ex); return null; }
    }

    public static void Save(BansaSettings settings)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            // Atomic write: serialize to a temp file, then swap it in. A crash mid-write
            // can only ever corrupt the temp file; the previous good version becomes .bak.
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(SettingsPath))
                File.Replace(tmp, SettingsPath, SettingsPath + ".bak");
            else
                File.Move(tmp, SettingsPath);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Debug("SettingsManager.Changed handler", ex); }
        }
        catch (Exception ex) { Log.Debug("SettingsManager.Save", ex); }
    }

    public static RateUnit GetRateUnit()
    {
        if (App.Settings is null) return RateUnit.Bytes;
        return App.Settings.RateUnit.Equals("Bits", StringComparison.OrdinalIgnoreCase)
            ? RateUnit.Bits
            : RateUnit.Bytes;
    }
}
