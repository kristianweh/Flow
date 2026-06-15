using System.Diagnostics;
using System.Text;

namespace Bansa.Services;

// ════════════════════════════════════════════════════════════════════════════
//  InterfacePriority  —  control which connection Windows prefers
//
//  Windows routes by the lowest interface metric. By default every adapter uses
//  an automatic metric derived from link speed, which is why Wi-Fi can win over
//  Ethernet. Pinning a low manual metric forces a chosen adapter to the front.
//
//  Unlike Bansa's firewall/QoS rules this is a PERSISTENT, global Windows change
//  (that is the point — it must survive reconnects). The pre-change state is
//  captured in settings so it can be fully restored.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>One adapter's IPv4/IPv6 metric state.</summary>
public sealed class IfaceMetric
{
    public string Alias    { get; set; } = "";
    public int    V4Metric { get; set; }
    public bool   V4Auto   { get; set; } = true;
    public int    V6Metric { get; set; }
    public bool   V6Auto   { get; set; } = true;

    /// <summary>The metric that actually decides routing (IPv4 default route).</summary>
    public int EffectiveMetric => V4Metric;
}

public static class InterfacePriority
{
    public const int PreferredMetric = 10;

    // ── Read ──────────────────────────────────────────────────────────────────

    public static List<IfaceMetric> Read()
    {
        const string cmd =
            "Get-NetIPInterface | Where-Object { $_.ConnectionState -eq 'Connected' -and " +
            "$_.InterfaceAlias -notlike 'Loopback*' } | " +
            "Select-Object InterfaceAlias,AddressFamily,InterfaceMetric,AutomaticMetric | " +
            "ConvertTo-Csv -NoTypeInformation";

        var byAlias = new Dictionary<string, IfaceMetric>(StringComparer.OrdinalIgnoreCase);
        var csv = RunPs(cmd);

        bool header = true;
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (header) { header = false; continue; } // skip "InterfaceAlias","AddressFamily",...

            var cols = ParseCsvLine(line);
            if (cols.Count < 4) continue;

            var alias  = cols[0];
            var family = cols[1];
            int.TryParse(cols[2], out int metric);
            bool auto = cols[3].Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                     || cols[3].Equals("True",    StringComparison.OrdinalIgnoreCase);

            if (!byAlias.TryGetValue(alias, out var m))
            {
                m = new IfaceMetric { Alias = alias };
                byAlias[alias] = m;
            }
            if (family.Equals("IPv6", StringComparison.OrdinalIgnoreCase))
            {
                m.V6Metric = metric; m.V6Auto = auto;
            }
            else
            {
                m.V4Metric = metric; m.V4Auto = auto;
            }
        }

        return byAlias.Values.OrderBy(m => m.EffectiveMetric).ToList();
    }

    /// <summary>
    /// The interface Windows would actually use to reach the internet right now —
    /// the authoritative "active connection" (the taskbar icon is NOT a reliable signal).
    /// </summary>
    public static string ActiveInternetAlias() =>
        RunPs("(Find-NetRoute -RemoteIPAddress 8.8.8.8 -ErrorAction SilentlyContinue | " +
              "Select-Object -First 1).InterfaceAlias").Trim();

    // ── Mutations ──────────────────────────────────────────────────────────────

    /// <summary>Pins one adapter to the front of the default route, backing up the prior state once.</summary>
    public static void SetPreferred(string alias, BansaSettings settings)
    {
        var current = Read();

        // Capture the original state the first time we change anything.
        if (settings.InterfacePriorityBackup.Count == 0)
        {
            foreach (var m in current)
                settings.InterfacePriorityBackup[m.Alias] = new InterfaceMetricBackup
                {
                    AutomaticV4 = m.V4Auto, MetricV4 = m.V4Metric,
                    AutomaticV6 = m.V6Auto, MetricV6 = m.V6Metric,
                };
        }

        // Pin the chosen adapter low and push every other one back to automatic,
        // otherwise a previously-preferred adapter stays at metric 10 and ties.
        foreach (var m in current)
        {
            if (string.Equals(m.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                SetMetric(alias, "IPv4", PreferredMetric);
                SetMetric(alias, "IPv6", PreferredMetric);
            }
            else
            {
                SetAutomatic(m.Alias, "IPv4");
                SetAutomatic(m.Alias, "IPv6");
            }
        }
        settings.PreferredAdapterAlias = alias;
    }

    /// <summary>Sets every connected interface back to automatic and clears the backup.</summary>
    public static void RevertToAutomatic(BansaSettings settings)
    {
        foreach (var m in Read())
        {
            SetAutomatic(m.Alias, "IPv4");
            SetAutomatic(m.Alias, "IPv6");
        }
        settings.InterfacePriorityBackup.Clear();
        settings.PreferredAdapterAlias = "";
    }

    /// <summary>Restores the exact metrics captured before the first change, then clears the backup.</summary>
    public static void RestorePrevious(BansaSettings settings)
    {
        foreach (var (alias, b) in settings.InterfacePriorityBackup)
        {
            if (b.AutomaticV4) SetAutomatic(alias, "IPv4"); else SetMetric(alias, "IPv4", b.MetricV4);
            if (b.AutomaticV6) SetAutomatic(alias, "IPv6"); else SetMetric(alias, "IPv6", b.MetricV6);
        }
        settings.InterfacePriorityBackup.Clear();
        settings.PreferredAdapterAlias = "";
    }

    // ── PowerShell plumbing ─────────────────────────────────────────────────────

    private static void SetMetric(string alias, string family, int metric) =>
        RunPs($"Set-NetIPInterface -InterfaceAlias '{Esc(alias)}' -AddressFamily {family} -InterfaceMetric {metric}");

    private static void SetAutomatic(string alias, string family) =>
        RunPs($"Set-NetIPInterface -InterfaceAlias '{Esc(alias)}' -AddressFamily {family} -AutomaticMetric Enabled");

    private static string Esc(string s) => s.Replace("'", "''");

    private static string RunPs(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            string err  = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (!string.IsNullOrWhiteSpace(err))
                Log.Debug("InterfacePriority.RunPs stderr", new Exception(err.Trim()));
            return outp;
        }
        catch (Exception ex)
        {
            Log.Debug("InterfacePriority.RunPs", ex);
            return "";
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
