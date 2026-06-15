using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Bansa.Models;

namespace Bansa.Services;

// ════════════════════════════════════════════════════════════════════════════
//  BindLaunchTracker  —  in-memory registry of apps launched via ForceBindIP
//
//  Binding is ephemeral: ForceBindIP only changes how the child process starts,
//  nothing is written to the OS. So this tracker lives only in memory, and the
//  full "undo" is simply stopping/closing the process (Stop()).
// ════════════════════════════════════════════════════════════════════════════

public enum BoundState { Starting, Running, Exited }

public enum BindSeverity { Ok, Warn, Info }

public sealed record BindStatus(BindSeverity Severity, string Text, string Proto = "");

public sealed class BoundLaunch
{
    public string ExePath     { get; init; } = "";
    public string ProcName    { get; init; } = "";   // lower-case, no extension
    public string BoundIp     { get; init; } = "";
    public string AdapterKind { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public HashSet<int> PreexistingPids { get; init; } = new();
    public int? Pid { get; set; }
}

public static class BindLaunchTracker
{
    private static readonly List<BoundLaunch> _active = new();
    private static readonly object _lock = new();

    /// <summary>Pids of the target exe already running, so Resolve() can pick out the new one.</summary>
    public static HashSet<int> SnapshotPids(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            set.Add(p.Id);
            p.Dispose();
        }
        return set;
    }

    public static BoundLaunch Register(string exePath, NetAdapter adapter, HashSet<int> preexistingPids)
    {
        var bl = new BoundLaunch
        {
            ExePath         = exePath,
            ProcName        = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant(),
            BoundIp         = adapter.Ipv4.ToString(),
            AdapterKind     = adapter.Kind,
            AdapterName     = adapter.Name,
            PreexistingPids = preexistingPids,
        };
        lock (_lock) _active.Add(bl);
        return bl;
    }

    public static List<BoundLaunch> Snapshot()
    {
        lock (_lock) return new List<BoundLaunch>(_active);
    }

    public static void Remove(BoundLaunch bl)
    {
        lock (_lock) _active.Remove(bl);
    }

    /// <summary>Resolves (and caches) the target PID and reports whether it is up yet.</summary>
    public static BoundState Resolve(BoundLaunch bl)
    {
        if (bl.Pid is int pid)
        {
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) return BoundState.Running; }
            catch { }
            return BoundState.Exited;
        }

        foreach (var p in Process.GetProcessesByName(bl.ProcName))
        {
            try
            {
                if (bl.PreexistingPids.Contains(p.Id)) continue;
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                if (path.Length == 0 || string.Equals(path, bl.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    bl.Pid = p.Id;
                    return BoundState.Running;
                }
            }
            finally { p.Dispose(); }
        }

        // ForceBindIP needs a moment to inject and start the target.
        return (DateTime.UtcNow - bl.StartedAt).TotalSeconds < 15 ? BoundState.Starting : BoundState.Exited;
    }

    /// <summary>Terminates the bound process — the complete reversal of a binding.</summary>
    public static bool Stop(BoundLaunch bl)
    {
        if (bl.Pid is not int pid) return false;
        try { using var p = Process.GetProcessById(pid); p.Kill(); return true; }
        catch { return false; }
    }

    // ── Verification ──────────────────────────────────────────────────────────
    // The proof a binding "took" is that the app's sockets carry the bound IP as
    // their LOCAL address. We read live connections (IP Helper API) and compare.

    public static BindStatus Verify(BoundLaunch bl, BoundState state,
                                    IReadOnlyDictionary<int, List<ConnectionInfo>> connsByPid)
    {
        if (state == BoundState.Starting) return new(BindSeverity.Info, "Starting…");
        if (state == BoundState.Exited)   return new(BindSeverity.Info, "Exited");

        if (bl.Pid is not int pid)
            return new(BindSeverity.Info, "Running · no active connections yet");

        // Multi-process apps (Electron/Chromium) do their networking in a child
        // "network service" process, not the launched parent — aggregate the whole
        // process tree so the status reflects what's really happening.
        var conns = new List<ConnectionInfo>();
        foreach (var p in DescendantPids(pid))
            if (connsByPid.TryGetValue(p, out var l)) conns.AddRange(l);
        if (conns.Count == 0)
            return new(BindSeverity.Info, "Running · no active connections yet");

        int match = 0, otherV4 = 0, v6 = 0;
        string? otherIp = null;

        foreach (var c in conns)
        {
            switch (c.Protocol)
            {
                case "TCP6":
                    // ForceBindIP binds the IPv4 source only; live IPv6 bypasses it.
                    if (c.State == "ESTABLISHED" && IsRoutableV6(c.RemoteAddress)) v6++;
                    continue;
                case "UDP6":
                    continue; // background UDP6 (SSDP, mDNS) is too noisy to flag
                case "TCP":
                    if (c.RemoteAddress is "0.0.0.0" or "") continue; // listener, not an outbound
                    break;
                case "UDP":
                    break;
                default:
                    continue;
            }

            if (!IPAddress.TryParse(c.LocalAddress, out var lip)) continue;
            if (IsIgnorableV4(lip)) continue;

            if (string.Equals(c.LocalAddress, bl.BoundIp, StringComparison.Ordinal)) match++;
            else { otherV4++; otherIp ??= c.LocalAddress; }
        }

        bool anyV4 = match > 0 || otherV4 > 0;
        string proto = (anyV4, v6 > 0) switch
        {
            (true,  true)  => "IPv4 + IPv6",
            (true,  false) => "IPv4",
            (false, true)  => "IPv6",
            _              => "",
        };

        if (match > 0 && otherV4 == 0 && v6 == 0)
            return new(BindSeverity.Ok, $"✓ Bound — {match} connection{(match == 1 ? "" : "s")} on {bl.BoundIp}", proto);
        if (match > 0)
            return new(BindSeverity.Warn, $"Mostly bound — some traffic via {(otherV4 > 0 ? otherIp : "IPv6")}", proto);
        if (otherV4 > 0)
            return new(BindSeverity.Warn, $"⚠ Not bound — using {otherIp}", proto);
        if (v6 > 0)
            return new(BindSeverity.Warn, "⚠ Using IPv6 — bypasses the bind", proto);

        return new(BindSeverity.Info, "Running · no active connections yet", proto);
    }

    private static bool IsIgnorableV4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 0                       // 0.0.0.0
            || b[0] == 127                     // loopback
            || (b[0] == 169 && b[1] == 254);   // APIPA / link-local
    }

    private static bool IsRoutableV6(string a)
        => a.Length > 0 && a != "::" && a != "::1"
           && !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)   // link-local
           && !a.StartsWith("ff", StringComparison.OrdinalIgnoreCase);    // multicast

    // ── Process tree (for multi-process apps) ─────────────────────────────────

    /// <summary>The bound PID plus all of its descendant PIDs.</summary>
    public static HashSet<int> DescendantPids(int root)
    {
        var result = new HashSet<int> { root };
        var children = new Dictionary<int, List<int>>();

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref pe))
            {
                do
                {
                    int pid  = (int)pe.th32ProcessID;
                    int ppid = (int)pe.th32ParentProcessID;
                    if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new List<int>();
                    list.Add(pid);
                }
                while (Process32Next(snap, ref pe));
            }
        }
        finally { CloseHandle(snap); }

        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (children.TryGetValue(p, out var kids))
                foreach (var k in kids)
                    if (result.Add(k)) queue.Enqueue(k);
        }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int  pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
