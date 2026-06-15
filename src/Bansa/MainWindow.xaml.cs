using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Bansa.Services;
using Bansa.ViewModels;
using Bansa.Views;

// Pin ambiguous types to their WPF variants (System.Drawing brings duplicates via UseWindowsForms).
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Bansa;

public partial class MainWindow : Window
{
    private TrayIconManager?    _tray;
    private FloatingGraphWindow? _floatingGraph;
    private ToolsViewModel?      _toolsVm;

    // Chart state
    private IReadOnlyList<(long Down, long Up)> _chartHistory = Array.Empty<(long, long)>();
    private IReadOnlyList<(string Name, string ImagePath, long DownBps, long UpBps)[]?> _appHistory
        = Array.Empty<(string Name, string ImagePath, long DownBps, long UpBps)[]?>();
    private long _chartPeakDown = 1, _chartPeakUp = 1;
    private bool _dualScale;

    // Crosshair — vertical line + popup panel on ChartOverlay
    private System.Windows.Shapes.Line? _crosshairLine;
    private Border? _crosshairPanel;
    private int _lastCrosshairIdx = -1;   // skip content rebuild when idx unchanged

    // Chart pause state
    private bool _chartPaused;

    // Chart scroll / drag state
    private int    _chartScrollOffset;         // 0 = live; > 0 = scrolled back in time (samples)
    private bool   _chartDragging;
    private double _chartDragStartX;
    private int    _chartDragStartOffset;
    private const int kChartWindow = 60;       // visible samples (30 s at 0.5 s/sample)

    // ── Temperature / usage ring buffers for hardware card sparklines ─────────
    // 60 samples × ~2 s per sample ≈ 2 minutes of history.
    // Ring buffer: _tempBufHead points to the NEXT write slot (oldest data is at head).
    private const int TempHistLen = 60;
    private readonly float[] _cpuTempBuf = new float[TempHistLen];
    private readonly float[] _gpuTempBuf = new float[TempHistLen];
    private readonly float[] _ramPctBuf  = new float[TempHistLen];
    private int _tempBufHead;    // next write index
    private int _tempBufCount;   // how many entries are valid (ramps up to TempHistLen)

    // Y-axis scale of the overlaid CPU/GPU timeline — cached so the crosshair maps Y identically.
    private float _ovSMin, _ovSMax = 1;

    // ── Ping sparkline ring buffer (Dashboard ping card) ─────────────────────
    private const int PingHistLen = 60;
    private readonly int[] _pingBuf = new int[PingHistLen];
    private int _pingBufHead;
    private int _pingBufCount;

    // Chart chrome (grid / labels / ticks / crosshair) resolves from the active theme's
    // resources on each redraw so it adapts to dark/light. These are cheap dictionary
    // lookups and the chart only redraws ~2×/s. Fallbacks match the original dark values.
    private static Brush ChartChrome(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? FrozenBrush(fallback);

    // Cached dash arrays — allocated once, reused across every chart redraw
    private static readonly DoubleCollection _dashFour  = Frozen(new DoubleCollection { 4, 4 });
    private static readonly DoubleCollection _dashSix   = Frozen(new DoubleCollection { 6, 3 });
    private static readonly DoubleCollection _dashTwo   = Frozen(new DoubleCollection { 2, 2 });

    private static DoubleCollection Frozen(DoubleCollection dc) { dc.Freeze(); return dc; }

    private static SolidColorBrush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Color-dependent chart brushes — rebuilt only when user changes color in settings
    private SolidColorBrush? _chartDownStroke, _chartDownFill, _chartUpStroke, _chartUpFill;
    private Color _cachedChartDown, _cachedChartUp;

    // Shutdown guard — prevents the FloatingGraphWindow.Closed inline handler from saving
    // ShowFloatingGraph=false while OnClosing is already handling the final correct save.
    private bool _isClosing;
    // Set by the tray "Quit" action so OnClosing knows to skip MinimizeOnClose.
    private bool _forceClose;

    // Global hotkey (Ctrl+Shift+<key>)
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Win32 window-style helpers — needed to keep WS_THICKFRAME|WS_CAPTION
    // intact (some WindowChrome configs strip them) so FancyZones can snap
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_STYLE    = -16;
    private const int WS_CAPTION   = 0x00C00000;
    private const int WS_THICKFRAME= 0x00040000;

    // UIPI message filter — allows lower-integrity processes (ShareX, FancyZones)
    // to send window-management messages to this elevated window
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);
    private const uint MSGFLT_ALLOW  = 1;
    private const uint WM_LBUTTONDOWN   = 0x0201;
    private const uint WM_LBUTTONUP     = 0x0202;
    private const uint WM_MOUSEMOVE     = 0x0200;
    private const uint WM_MOVING        = 0x0216;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE  = 0x0232;
    private const uint WM_SIZING        = 0x0214;
    private const int    _hotKeyId    = 9001;
    private const uint   MOD_CONTROL  = 0x0002;
    private const uint   MOD_SHIFT    = 0x0004;
    private const int    WM_HOTKEY    = 0x0312;
    private uint         _hotkeyVk    = 0x46;   // 'F' by default; overridden from settings

    // Hotkey capture state
    private bool _capturingHotkey;

    // Curated palette for swatch pickers
    private static readonly string[] _palette =
    {
        "#5DADE2", "#3B82F6", "#5865F2", "#8B5CF6", "#EC4899",
        "#EF4444", "#F59E0B", "#F39C12", "#10B981", "#06B6D4",
        "#94A3B8",
    };

    // Temperature-band palette: cool blues/cyans → greens → warm yellows/oranges → reds
    // (includes the three band defaults so the active swatch always highlights).
    private static readonly string[] _tempPalette =
    {
        "#36BFFA", "#3B82F6", "#06B6D4", "#22D3EE", "#10B981", "#A3E635",
        "#FFD60A", "#FACC15", "#F59E0B", "#FB923C", "#FF3B30", "#EF4444",
    };

    // Domain-accent palette: full cool→warm spectrum so Network and Hardware can each pick
    // any color (not just cool / warm). Includes both domain defaults (#00C8F0 cyan,
    // #FF8A3D thermal) so whichever is active always highlights its selected swatch.
    private static readonly string[] _domainPalette =
    {
        "#00C8F0", "#2D9CFF", "#3B82F6", "#5865F2", "#8B5CF6", "#EC4899",
        "#EF4444", "#FF5C5C", "#FF8A3D", "#F97316", "#F59E0B", "#FACC15",
        "#10B981", "#06B6D4", "#94A3B8",
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowsAccent.TryApplyMica(hwnd, dark: ThemeManager.Current == AppTheme.Dark);
        ThemeManager.ThemeChanged += t => WindowsAccent.TryApplyMica(hwnd, t == AppTheme.Dark);

        // Ensure WS_CAPTION | WS_THICKFRAME are present.
        // WindowChrome can strip these on some WPF versions; FancyZones requires
        // both to detect the window as moveable and show snap zones.
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION | WS_THICKFRAME);

        // UIPI fix: allow lower-integrity processes (ShareX, FancyZones running
        // as a standard user) to deliver window-management messages to this
        // elevated window.  Without this, hooks in non-elevated apps are silently
        // blocked when Bansa is in the foreground.
        foreach (var msg in new uint[]
            { WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
              WM_MOVING, WM_ENTERSIZEMOVE, WM_EXITSIZEMOVE, WM_SIZING })
        {
            ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
        }

        // HotkeyVirtualKey == 0 means the user explicitly cleared the hotkey.
        // Default in BansaSettings is 0x46 ('F'), so new users get Ctrl+Shift+F automatically.
        _hotkeyVk = (uint)App.Settings.HotkeyVirtualKey;
        if (_hotkeyVk > 0)
            RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, _hotkeyVk);

        // Hook WndProc so we can return the right NCHITTEST codes.
        // WindowChrome handles sizing in its own hook but the caption drag can be
        // unreliable when controls in the title bar row consume the mouse.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private const int WM_NCHITTEST   = 0x0084;
    private const int HTCLIENT       = 1;
    private const int HTCAPTION      = 2;
    private const int HTLEFT         = 10;
    private const int HTRIGHT        = 11;
    private const int HTTOP          = 12;
    private const int HTTOPLEFT      = 13;
    private const int HTTOPRIGHT     = 14;
    private const int HTBOTTOM       = 15;
    private const int HTBOTTOMLEFT   = 16;
    private const int HTBOTTOMRIGHT  = 17;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotKeyId)
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                Hide();
            else { Show(); WindowState = WindowState.Normal; Activate(); }
            handled = true;
            return IntPtr.Zero;
        }

        // A second Bansa instance was launched and bowed out — bring this one forward.
        if (msg == App.ShowMainWindowMessage)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // lParam is packed screen-pixel coords (signed 16-bit each half)
        int raw   = lParam.ToInt32();
        int scrX  = (short)(raw & 0xFFFF);
        int scrY  = (short)((raw >> 16) & 0xFFFF);

        // Convert screen pixels → WPF device-independent pixels
        var pt = PointFromScreen(new System.Windows.Point(scrX, scrY));
        double x = pt.X, y = pt.Y;
        double w = ActualWidth, h = ActualHeight;

        // Skip resize handling when maximized — edges are hidden under taskbar/screen edges
        bool canResize = ResizeMode == ResizeMode.CanResize ||
                         ResizeMode == ResizeMode.CanResizeWithGrip;
        double border = canResize && WindowState != WindowState.Maximized ? 6.0 : 0.0;

        // Corners (checked first so they take priority over edges)
        if (border > 0)
        {
            if (x <      border && y <      border) { handled = true; return (IntPtr)HTTOPLEFT;     }
            if (x > w - border  && y <      border) { handled = true; return (IntPtr)HTTOPRIGHT;    }
            if (x <      border && y > h - border)  { handled = true; return (IntPtr)HTBOTTOMLEFT;  }
            if (x > w - border  && y > h - border)  { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
            if (x <      border)                    { handled = true; return (IntPtr)HTLEFT;         }
            if (x > w - border)                     { handled = true; return (IntPtr)HTRIGHT;        }
            if (y <      border)                    { handled = true; return (IntPtr)HTTOP;           }
            if (y > h - border)                     { handled = true; return (IntPtr)HTBOTTOM;       }
        }

        // Title bar — let WindowChrome handle HTCAPTION so FancyZones'
        // WM_NCHITTEST chain fires correctly. We only override the edges.
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBrandImages(ThemeManager.Current);
        ThemeManager.ThemeChanged += ApplyBrandImages;

        ReparentLimitsCards();

        // Reflect the persisted domain (accent already applied at startup in App.OnStartup)
        DomainHardwareBtn.IsChecked = DomainManager.Current == AppDomainMode.Hardware;
        DomainNetworkBtn.IsChecked  = DomainManager.Current == AppDomainMode.Network;
        UpdateSidebarForDomain(DomainManager.Current);
        if (DomainManager.Current == AppDomainMode.Hardware) NavigateToPanel(2);

        UnitBitsRadio.IsChecked  = Vm.UseBitsUnit;
        UnitBytesRadio.IsChecked = !Vm.UseBitsUnit;

        // Restore dual-scale toggle state from previous session
        _dualScale = App.Settings.DualScale;
        DualScaleBtn.IsChecked = _dualScale;

        // Accent is owned by DomainManager (per-domain dominant color, theme-aware) —
        // already applied at startup; nothing to do here.

        // Apply saved colors to override theme defaults before swatches are built
        Vm.SetDownColor(App.Settings.DownColorHex);
        Vm.SetUpColor(App.Settings.UpColorHex);
        Vm.SetCpuColor(App.Settings.CpuColorHex);
        Vm.SetGpuColor(App.Settings.GpuColorHex);
        Vm.SetRamColor(App.Settings.RamColorHex);

        PopulateSwatches(DownGraphSwatches,    App.Settings.DownColorHex,     hex => Vm.SetDownColor(hex));
        PopulateSwatches(UpGraphSwatches,      App.Settings.UpColorHex,       hex => Vm.SetUpColor(hex));
        PopulateSwatches(CpuColorSwatches,     App.Settings.CpuColorHex,      hex => Vm.SetCpuColor(hex));
        PopulateSwatches(GpuColorSwatches,     App.Settings.GpuColorHex,      hex => Vm.SetGpuColor(hex));
        PopulateSwatches(RamColorSwatches,     App.Settings.RamColorHex,      hex => Vm.SetRamColor(hex));
        SetupTempBandSwatches();
        PopulateSwatches(PingGoodSwatches,     App.Settings.PingGoodColorHex, hex => Vm.SetPingGoodColor(hex));
        PopulateSwatches(PingBadSwatches,      App.Settings.PingBadColorHex,  hex => Vm.SetPingBadColor(hex));
        PopulateSwatches(NetworkAccentSwatches,  _domainPalette, App.Settings.NetworkColorHex,  hex => SetDomainColor(AppDomainMode.Network, hex));
        PopulateSwatches(HardwareAccentSwatches, _domainPalette, App.Settings.HardwareColorHex, hex => SetDomainColor(AppDomainMode.Hardware, hex));

        try
        {
            _tray = new TrayIconManager(this, onQuit: () =>
            {
                _forceClose = true;
                Application.Current.Shutdown();
            });
            Vm.TraySnapshot += (down, up, ping, history, apps) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    // `apps` is already the filtered+sorted AppsView snapshot from the VM,
                    // so it respects KB/s threshold, text filter, Hide-local, etc.
                    var appList = apps.ToList();
                    try { _tray?.Update(down, up, ping, history, appList); } catch { }

                    // Only redraw what is actually on screen. Hidden panels repaint within
                    // one tick (≤500 ms) of becoming visible, so nothing is ever stale —
                    // this just stops canvas rebuilds for UI nobody can see (window in
                    // tray, or a different panel active).
                    if (IsVisible)
                    {
                        if (!_chartPaused && ProcPanel.Visibility == Visibility.Visible)
                        {
                            _appHistory = Vm.AppTickSnapshot();
                            DrawMainChart(history);
                        }
                        if (DashboardPanel.Visibility == Visibility.Visible)
                        {
                            RedrawBandwidthDonut(appList);
                            RedrawDashThroughput(history);
                        }
                        // Keep ping color consistent across every window (also records the
                        // ping ring-buffer sample, so it must run on every tick).
                        UpdatePingColor(ping);
                    }
                    else
                    {
                        RecordPingSample(ping);   // keep the sparkline history continuous
                    }

                    _floatingGraph?.UpdateChart(
                        history,
                        ParseColor(App.Settings?.DownColorHex, Color.FromRgb(0x5D, 0xAD, 0xE2)),
                        ParseColor(App.Settings?.UpColorHex,   Color.FromRgb(0xF3, 0x9C, 0x12)),
                        ping,
                        appList);
                });
            };
        }
        catch (Exception ex)
        {
            Vm.StatusText = "Tray icon failed: " + ex.Message;
        }

        // Force a layout pass when the first ETW data arrives so the DataGrid renders rows immediately.
        // CollectionChanged fires INSIDE DeferRefresh so we must dispatch at Background priority,
        // which runs after DeferRefresh ends and the view is fully sorted/filtered.
        System.Collections.Specialized.NotifyCollectionChangedEventHandler? firstData = null;
        firstData = (_, e) =>
        {
            if (e.NewItems?.Count > 0)
            {
                Vm.Apps.CollectionChanged -= firstData;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    AppGrid.Items.Refresh();
                    DashAppGrid.Items.Refresh();
                    _ = Vm.TriggerHourlyRefreshAsync();
                }));
            }
        };
        Vm.Apps.CollectionChanged += firstData;

        // Show saved hotkey binding label (0 = cleared, > 0 = active key)
        if (App.Settings.HotkeyVirtualKey > 0)
        {
            try
            {
                var k = KeyInterop.KeyFromVirtualKey(App.Settings.HotkeyVirtualKey);
                HotkeyLabel.Text = $"Ctrl+Shift+{k}";
            }
            catch { HotkeyLabel.Text = $"Ctrl+Shift+VK{App.Settings.HotkeyVirtualKey:X2}"; }
        }
        else
        {
            HotkeyLabel.Text = "None";
        }

        // Ensure the active ping target is in the list (handles legacy settings)
        if (!App.Settings.PingTargets.Contains(App.Settings.PingTarget, StringComparer.OrdinalIgnoreCase))
        {
            App.Settings.PingTargets.Insert(0, App.Settings.PingTarget);
            SettingsManager.Save(App.Settings);
        }
        PopulatePingTargetCombo();

        // Restore main window bounds
        RestoreWindowBounds();

        // Restore Network tab chart height
        RestoreChartHeight();

        // Restore sort column + direction and column widths/visibility
        RestoreAppGridSort();
        RestoreAppGridColumns();

        // Open floating graph if it was visible in the last session
        if (App.Settings.ShowFloatingGraph)
            OpenFloatingGraph();

        // Init window-behaviour and auto-start toggles
        InitBehaviourToggles();
        InitAutoStartToggle();

        // Start minimized if the user set that preference
        if (App.Settings.StartMinimizedToTray)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => WindowState = WindowState.Minimized));

        // Wire hardware monitor panel live updates
        InitHardwarePanel();

        // Wire Tools panel
        _toolsVm = new ToolsViewModel();
        ToolsPanel.DataContext = _toolsVm;

        // Network Tools panel needs the shared VM for the known-apps catalog.
        NetworkToolsPanel.Init(Vm);

        // First-run reversibility explainer — shown once. Deferred to Background priority so the
        // main window paints first; skipped when starting hidden to the tray.
        if (!App.Settings.WelcomeDismissed && !App.Settings.StartMinimizedToTray)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                try { new Views.WelcomeWindow { Owner = this }.ShowDialog(); }
                catch (Exception ex) { Log.Debug("Welcome dialog failed", ex); }
                App.Settings.WelcomeDismissed = true;
                SettingsManager.Save(App.Settings);
            }));
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
        UpdateMaxButtonGlyph();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If "minimize on close" is on and this isn't a deliberate quit, send to tray instead.
        if (App.Settings.MinimizeOnClose && !_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Signal that we're shutting down — the FloatingGraphWindow.Closed inline handler
        // checks this flag and skips its ShowFloatingGraph=false save so we can write the
        // correct value at the very end of this method.
        _isClosing = true;

        // Capture BEFORE anything closes.
        bool floatWasOpen = _floatingGraph?.IsVisible == true;

        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, _hotKeyId); } catch { }
        try { _floatingGraph?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        // _toolsVm has no disposable resources
        try { Vm.Dispose(); } catch { }

        // Persist column layout before final settings write
        SaveAppGridColumns();

        // Save main window bounds
        SaveWindowBounds();

        // Save ShowFloatingGraph LAST so it wins over any value written by FloatingGraphWindow.Closed
        App.Settings.ShowFloatingGraph = floatWasOpen;
        SettingsManager.Save(App.Settings);
    }

    // ────────── Title-bar drag ──────────

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void RestoreWindowBounds()
    {
        var s = App.Settings;
        if (s.MainWindowW > 0 && s.MainWindowH > 0)
        {
            Width  = s.MainWindowW;
            Height = s.MainWindowH;
        }
        if (s.MainWindowX >= 0 && s.MainWindowY >= 0)
        {
            // Clamp to visible screen area
            var screen = System.Windows.SystemParameters.WorkArea;
            Left = Math.Max(screen.Left, Math.Min(s.MainWindowX, screen.Right  - Width));
            Top  = Math.Max(screen.Top,  Math.Min(s.MainWindowY, screen.Bottom - Height));
        }
        if (s.MainWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        // Only save Normal state bounds (don't overwrite with maximized coords)
        if (WindowState == WindowState.Normal)
        {
            App.Settings.MainWindowX = Left;
            App.Settings.MainWindowY = Top;
            App.Settings.MainWindowW = Width;
            App.Settings.MainWindowH = Height;
        }
        App.Settings.MainWindowMaximized = WindowState == WindowState.Maximized;
    }

    // ── Network tab chart height persistence ─────────────────────────────────

    private void RestoreChartHeight()
    {
        double h = App.Settings.NetworkChartHeight;
        if (h >= 60 && h <= 600)
            ProcPanel.RowDefinitions[2].Height = new GridLength(h);
    }

    private void OnChartSplitterDragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        double h = ProcPanel.RowDefinitions[2].ActualHeight;
        if (h >= 60)
        {
            App.Settings.NetworkChartHeight = h;
            SettingsManager.Save(App.Settings);
        }
    }

    // ────────── Caption buttons ──────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object sender, RoutedEventArgs e)
        => WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCloseEnter(object sender, MouseEventArgs e)
        => CloseBtn.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private void OnCloseLeave(object sender, MouseEventArgs e) => CloseBtn.ClearValue(BackgroundProperty);

    private void UpdateMaxButtonGlyph()
    {
        if (MaxBtn is null) return;
        //  = Chrome Restore,  = Chrome Maximize (Segoe Fluent / MDL2)
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ────────── Sidebar nav ──────────
    // Tags: 0=Dashboard, 1=Network, 2=Hardware, 3=History, 4=Settings

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out int idx)) return;
        NavigateToPanel(idx);
    }

    // "View all →" on dashboard routes to Network tab
    private void OnDashViewAllClick(object sender, RoutedEventArgs e) => NavigateToPanel(1);

    // Header domain switch: re-skin accent + jump to that domain's primary panel.
    private void OnDomainClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<AppDomainMode>(tag, out var mode)) return;
        if (DomainManager.Current == mode) return;

        DomainManager.Apply(mode);
        App.Settings.Domain = mode.ToString();
        SettingsManager.Save(App.Settings);

        UpdateSidebarForDomain(mode);
        NavigateToPanel(mode == AppDomainMode.Hardware ? 2 : 0);
    }

    // ── Navigation helpers ────────────────────────────────────────────────────

    private static readonly Duration _fadeInDuration  = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration _fadeOutDuration = new(TimeSpan.FromMilliseconds(110));

    /// <summary>
    /// Fade + slide-up panel entrance.  The panel rises 14 px while fading in,
    /// giving the UI a spatial "new content arriving from below" feel.
    /// Respects the Windows "Show animations" accessibility setting.
    /// </summary>
    private static void FadeIn(UIElement el)
    {
        el.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            el.Opacity = 1;
            el.RenderTransform = new TranslateTransform(0, 0);
            return;
        }

        el.Opacity = 0;

        // Per-instance TranslateTransform so panels don't share a reference.
        var translate = new TranslateTransform(0, 14);
        el.RenderTransform = translate;
        el.RenderTransformOrigin = new Point(0.5, 0.5);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, _fadeInDuration) { EasingFunction = easing };
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        var slide = new DoubleAnimation(14, 0, _fadeInDuration) { EasingFunction = easing };
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Quick fade-out (110 ms) with a subtle downward drift — the reverse
    /// of FadeIn so outgoing content appears to "sink" as it leaves.
    /// </summary>
    private static void FadeOut(UIElement el)
    {
        if (!SystemParameters.ClientAreaAnimation) { el.Visibility = Visibility.Collapsed; return; }

        var translate = el.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
        el.RenderTransform = translate;

        var fade = new DoubleAnimation(el.Opacity, 0, _fadeOutDuration);
        fade.Completed += (_, _) => el.Visibility = Visibility.Collapsed;
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        var slide = new DoubleAnimation(0, 6, _fadeOutDuration);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Switch to a top-level panel by index (0=Dashboard,1=Network,2=Hardware,3=Tools,4=History,5=Settings).</summary>
    private void NavigateToPanel(int idx)
    {
        NavDashboard.IsChecked = idx == 0;
        NavProcesses.IsChecked = idx == 1;
        NavHardware.IsChecked  = idx == 2;
        NavTools.IsChecked     = idx == 3;
        NavHistory.IsChecked   = idx == 4;
        NavLimits.IsChecked    = idx == 6;
        NavNetworkTools.IsChecked = idx == 7;

        // History is domain-aware: Network history vs Hardware history.
        bool hw = DomainManager.Current == AppDomainMode.Hardware;
        UIElement historyEl = hw ? HardwareHistoryPanel : HistoryPanel;
        UIElement otherHistory = hw ? HistoryPanel : HardwareHistoryPanel;
        if (otherHistory.Visibility == Visibility.Visible) FadeOut(otherHistory);

        UIElement[] panels = [DashboardPanel, ProcPanel, HardwareMonitorPanel, ToolsPanel, historyEl, SettingsPanel, LimitsScenariosPanel, NetworkToolsPanel];
        for (int i = 0; i < panels.Length; i++)
        {
            if (i == idx)
            {
                if (panels[i].Visibility != Visibility.Visible) FadeIn(panels[i]);
            }
            else
            {
                if (panels[i].Visibility == Visibility.Visible) FadeOut(panels[i]);
            }
        }

        // Refresh history data whenever the user navigates to that tab.
        if (idx == 4)
        {
            if (hw) HardwareHistoryPanel.Reload();
            else    HistoryPanel.Reload();
        }

        // The Hardware panel skips its 2 s updates while hidden — repaint immediately
        // on entry so the user never sees stale gauges. (FadeIn has already set
        // Visibility=Visible, so the gate inside UpdateHardwarePanel passes.)
        if (idx == 2 && HardwareMonitor.Instance is { } hwMon && hwMon.Latest != HardwareSnapshot.Empty)
            UpdateHardwarePanel(hwMon.Latest, pushBuffers: false);

        // Limits & Scenarios: refresh its moved cards + the limited-apps summary.
        if (idx == 6)
        {
            RefreshProfilesList();
            PopulatePingTargetCombo();
            RefreshScenarioUI();
            PopulateLimitedApps();
            RefreshConnectionSpeedUI();
        }
    }

    private void OnSettingsGearClick(object sender, RoutedEventArgs e) => NavigateToPanel(5);

    /// <summary>Show only the nav items relevant to the active domain (mockup-style per-mode tabs).</summary>
    private void UpdateSidebarForDomain(AppDomainMode mode)
    {
        bool net = mode == AppDomainMode.Network;
        NavDashboard.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        NavProcesses.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        NavLimits.Visibility    = net ? Visibility.Visible : Visibility.Collapsed;
        NavNetworkTools.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        NavHardware.Visibility  = net ? Visibility.Collapsed : Visibility.Visible;
        // Tools: Hardware mode only. Network tabs = Dashboard · Live Traffic · Limits & Scenarios · History.
        NavTools.Visibility     = net ? Visibility.Collapsed : Visibility.Visible;
        // History stays visible in both modes.
    }

    // ── Dashboard card click handlers ────────────────────────────────────────

    private void OnDashBandwidthClick(object sender, MouseButtonEventArgs e) => NavigateToPanel(1);
    private void OnDashPingClick(object sender, MouseButtonEventArgs e)      => NavigateToLimitsCard(CardPingMonitor);

    // ── Sidebar STATUS card clicks → respective tabs ─────────────────────────

    /// <summary>CPU/GPU box → Hardware dashboard (switches domain so accent + nav follow).</summary>
    private void OnSidebarHwClick(object sender, MouseButtonEventArgs e)
    {
        EnsureDomain(AppDomainMode.Hardware);
        NavigateToPanel(2);
    }

    /// <summary>Down/Up totals box → Network dashboard.</summary>
    private void OnSidebarBandwidthClick(object sender, MouseButtonEventArgs e)
    {
        EnsureDomain(AppDomainMode.Network);
        NavigateToPanel(0);
    }

    /// <summary>Ping box → Ping Monitor card in Limits &amp; Scenarios.</summary>
    private void OnSidebarPingClick(object sender, MouseButtonEventArgs e)
        => NavigateToLimitsCard(CardPingMonitor);

    /// <summary>
    /// Applies a domain (accent reskin + per-domain sidebar + header toggle state) WITHOUT
    /// navigating — callers decide the destination panel. No-op when already in that domain.
    /// </summary>
    private void EnsureDomain(AppDomainMode mode)
    {
        if (DomainManager.Current == mode) return;
        DomainManager.Apply(mode);
        App.Settings.Domain = mode.ToString();
        SettingsManager.Save(App.Settings);
        UpdateSidebarForDomain(mode);
        DomainHardwareBtn.IsChecked = mode == AppDomainMode.Hardware;
        DomainNetworkBtn.IsChecked  = mode == AppDomainMode.Network;
    }

    /// <summary>Ensures Network domain, opens Limits &amp; Scenarios (panel 6) and scrolls to a card.</summary>
    private void NavigateToLimitsCard(FrameworkElement card)
    {
        EnsureDomain(AppDomainMode.Network);
        NavigateToPanel(6);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => card.BringIntoView()));
    }

    // Keyboard activation for the click-only dashboard / sidebar cards. Plain Borders aren't
    // focusable or Enter/Space-activatable, so each accessible card carries a Tag naming its
    // action and this raises the same navigation / toggle a mouse click would.
    private void OnCardKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string action) return;
        e.Handled = true;
        switch (action)
        {
            case "nav:network":   NavigateToPanel(1); break;
            case "nav:dashboard": EnsureDomain(AppDomainMode.Network); NavigateToPanel(0); break;
            case "nav:hardware":  EnsureDomain(AppDomainMode.Hardware); NavigateToPanel(2); break;
            case "ping":          NavigateToLimitsCard(CardPingMonitor); break;
            case "scenario":      _ = Vm.ToggleScenarioCommand.ExecuteAsync(null); break;
            case "globalcap":     ToggleGlobalCap(); break;
        }
    }

    // ────────── Floating graph ──────────

    private void OnFloatGraphToggle(object sender, RoutedEventArgs e)
    {
        if (_floatingGraph?.IsVisible == true)
        {
            _floatingGraph.Hide();
            Vm.ShowFloatingGraph = false;
        }
        else
        {
            OpenFloatingGraph();
            Vm.ShowFloatingGraph = true;
        }
    }

    private void OpenFloatingGraph()
    {
        if (_floatingGraph is null)
        {
            _floatingGraph = new FloatingGraphWindow();
            _floatingGraph.Closed += (_, _) =>
            {
                _floatingGraph = null;
                // Only save "closed" when the USER closed the float graph while app is running.
                // During app shutdown (_isClosing=true), OnClosing handles the final save.
                if (!_isClosing)
                    Vm.ShowFloatingGraph = false;
            };
        }
        _floatingGraph.Show();
        Vm.ShowFloatingGraph = true;
    }
}
