using System.IO;
using System.Text;

namespace Pulso.Features;

public sealed partial class ProcessMonitor
{
    const int PageSize = 4096;
    const long TicksTo100ns = 100_000; // CLK_TCK = 100 → 1 tick = 10 ms = 100 000 × 100 ns

    /// <summary>Linux: lê /proc/[pid]/{stat,statm,io,comm}. Equivale ao NtQuerySystemInformation do Windows.</summary>
    List<ProcInfo> LinuxSnapshot()
    {
        var list = new List<ProcInfo>(400);
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            string n = System.IO.Path.GetFileName(dir);
            if (n.Length == 0 || !char.IsDigit(n[0]) || !int.TryParse(n, out int pid)) continue;
            try
            {
                string stat = File.ReadAllText(dir + "/stat");
                // "pid (comm com espaços) S ppid ..." — o nome vai até o último ')'
                int close = stat.LastIndexOf(')');
                if (close < 0) continue;
                var f = stat[(close + 2)..].Split(' ');
                // f[0]=state ... f[11]=utime f[12]=stime (campos 14 e 15 do stat)
                if (f.Length < 13) continue;
                if (f[0] == "Z") continue; // zumbi
                long ticks = long.Parse(f[11]) + long.Parse(f[12]);

                double priv = 0;
                try
                {
                    var m = File.ReadAllText(dir + "/statm").Split(' ');
                    long resident = long.Parse(m[1]), shared = long.Parse(m[2]);
                    priv = Math.Max(0, resident - shared) * (double)PageSize;
                }
                catch { }

                long io = 0;
                try
                {
                    long rd = 0, wr = 0;
                    foreach (var l in File.ReadLines(dir + "/io"))
                    {
                        if (l.StartsWith("read_bytes:", StringComparison.Ordinal)) rd = long.Parse(l[11..].Trim());
                        else if (l.StartsWith("write_bytes:", StringComparison.Ordinal)) wr = long.Parse(l[12..].Trim());
                    }
                    io = rd + wr;
                }
                catch { }

                string image = LinuxImage(dir, stat, close);
                list.Add(new ProcInfo(pid, image, ticks * TicksTo100ns, io, priv));
            }
            catch { /* processo terminou durante a leitura */ }
        }
        return list;
    }

    static string LinuxImage(string dir, string stat, int close)
    {
        string comm = stat[(stat.IndexOf('(') + 1)..close];
        // comm é cortado em 15 caracteres: o nome do executável é mais fiel.
        if (comm.Length >= 15)
        {
            try
            {
                var exe = new FileInfo(dir + "/exe").LinkTarget;
                if (!string.IsNullOrEmpty(exe)) return System.IO.Path.GetFileName(exe.Replace(" (deleted)", ""));
            }
            catch { }
            try
            {
                var cmd = File.ReadAllText(dir + "/cmdline").Split('\0')[0];
                if (cmd.Length > 0) return System.IO.Path.GetFileName(cmd);
            }
            catch { }
        }
        return comm;
    }

    (string name, string path) LinuxDescribe(string image, int pid)
    {
        if (_names.TryGetValue(image, out var d)) return d;
        string path = "";
        try { path = new FileInfo($"/proc/{pid}/exe").LinkTarget?.Replace(" (deleted)", "") ?? ""; } catch { }
        string name = image;
        if (path != "") _names[image] = (name, path);
        return (name, path);
    }

    static readonly HashSet<string> LinuxCritical = new(StringComparer.Ordinal)
    {
        "systemd", "init", "Xorg", "Xwayland", "gnome-shell", "kwin_x11", "kwin_wayland", "plasmashell", "dbus-daemon", "dbus-broker",
        "pipewire", "wireplumber", "pulseaudio", "NetworkManager", "sshd", "login", "gdm", "gdm3", "sddm", "lightdm", "polkitd", "udevd", "systemd-udevd",
    };
}
