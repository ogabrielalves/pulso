using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Programas que abrem com o Windows: chaves Run, pastas Inicializar e tarefas agendadas de logon.
/// Desativar usa o mesmo mecanismo do Gerenciador de Tarefas (StartupApproved) e nunca apaga a entrada,
/// então reativar sempre volta exatamente como estava.
/// </summary>
public static class StartupApps
{
    public enum Source { RunUser, RunMachine, RunMachine32, FolderUser, FolderCommon, Task }

    public sealed class Item
    {
        public string Name = "", Command = "", ExePath = "", Publisher = "", Description = "";
        public Source Source;
        public bool Enabled;
        public bool Missing;             // o arquivo do programa não existe mais
        public double? SlowBootSec;      // o Windows registrou que este app atrasou o boot
        internal string Key = "";        // nome do valor / arquivo / caminho da tarefa
        public bool NeedsAdmin => Source is not (Source.RunUser or Source.FolderUser);
        public string DisplayName => Description.Length > 0 && Description.Length < 60 ? Description : Name;
        public string Where => Source switch
        {
            Source.RunUser => Loc.T("Registro (usuário)", "Registry (user)"),
            Source.RunMachine or Source.RunMachine32 => Loc.T("Registro (todos os usuários)", "Registry (all users)"),
            Source.FolderUser => Loc.T("Pasta Inicializar", "Startup folder"),
            Source.FolderCommon => Loc.T("Pasta Inicializar (todos)", "Startup folder (all users)"),
            _ => Loc.T("Tarefa agendada", "Scheduled task"),
        };
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Entradas do próprio Pulso ficam na tela Configurações → Iniciar com o Windows.
    static bool IsPulso(string name) => name.Equals("Pulso", StringComparison.OrdinalIgnoreCase);

    public static List<Item> Read()
    {
        var list = new List<Item>();
        ReadRun(list, Registry.CurrentUser, RegistryView.Default, Source.RunUser, Registry.CurrentUser, "Run");
        ReadRun(list, Registry.LocalMachine, RegistryView.Registry64, Source.RunMachine, Registry.LocalMachine, "Run");
        ReadRun(list, Registry.LocalMachine, RegistryView.Registry32, Source.RunMachine32, Registry.LocalMachine, "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Source.FolderUser, Registry.CurrentUser);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Source.FolderCommon, Registry.LocalMachine);
        ReadTasks(list);

        var cache = new Dictionary<string, FileVersionInfo?>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in list)
        {
            if (it.ExePath == "") it.ExePath = ExeFromCommand(it.Command);
            if (it.ExePath == "") continue;
            if (!File.Exists(it.ExePath)) { it.Missing = true; continue; }
            if (!cache.TryGetValue(it.ExePath, out var vi))
            {
                try { vi = FileVersionInfo.GetVersionInfo(it.ExePath); } catch { vi = null; }
                cache[it.ExePath] = vi;
            }
            it.Publisher = vi?.CompanyName?.Trim() ?? "";
            it.Description = vi?.FileDescription?.Trim() ?? "";
        }
        ApplySlowBoot(list);
        return list.OrderBy(i => !i.Enabled).ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void ReadRun(List<Item> list, RegistryKey hive, RegistryView view, Source src, RegistryKey approvedHive, string approvedName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);
            using var k = root.OpenSubKey(RunKey);
            if (k == null) return;
            using var ap = approvedHive.OpenSubKey(Approved + approvedName);
            foreach (var name in k.GetValueNames())
            {
                if (name == "" || IsPulso(name)) continue;
                list.Add(new Item
                {
                    Name = name, Key = name, Source = src,
                    Command = k.GetValue(name)?.ToString() ?? "",
                    Enabled = IsApproved(ap?.GetValue(name) as byte[]),
                });
            }
        }
        catch (Exception ex) { Paths.LogError("StartupApps.Run " + src, ex); }
    }

    static void ReadFolder(List<Item> list, string dir, Source src, RegistryKey approvedHive)
    {
        try
        {
            if (dir == "" || !Directory.Exists(dir)) return;
            using var ap = approvedHive.OpenSubKey(Approved + "StartupFolder");
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var it = new Item { Name = Path.GetFileNameWithoutExtension(f), Key = file, Source = src, Command = f, Enabled = IsApproved(ap?.GetValue(file) as byte[]) };
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(f) is string t) { it.ExePath = t; it.Command = t; }
                else it.ExePath = f;
                list.Add(it);
            }
        }
        catch (Exception ex) { Paths.LogError("StartupApps.Folder " + src, ex); }
    }

    static void ReadTasks(List<Item> list)
    {
        try
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            svc.Connect();
            void Walk(dynamic folder)
            {
                string path = folder.Path;
                if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
                var tasks = folder.GetTasks(1 /* TASK_ENUM_HIDDEN */);
                foreach (var t in tasks)
                {
                    try
                    {
                        string name = t.Name;
                        if (IsPulso(name)) continue;
                        var def = t.Definition;
                        bool logon = false;
                        foreach (var tr in def.Triggers) if ((int)tr.Type is 8 or 9) { logon = true; break; } // BOOT / LOGON
                        if (!logon) continue;
                        string exe = "", args = "";
                        foreach (var a in def.Actions) if ((int)a.Type == 0) { exe = a.Path ?? ""; args = a.Arguments ?? ""; break; }
                        if (exe == "") continue;
                        exe = Environment.ExpandEnvironmentVariables(exe.Trim('"'));
                        list.Add(new Item { Name = name, Key = (string)t.Path, Source = Source.Task, Command = (exe + " " + args).Trim(), ExePath = exe, Enabled = (bool)t.Enabled });
                    }
                    catch { }
                }
                foreach (var sub in folder.GetFolders(0)) Walk(sub);
            }
            Walk(svc.GetFolder(@"\"));
        }
        catch (Exception ex) { Paths.LogError("StartupApps.Tasks", ex); }
    }

    /// <summary>O Gerenciador de Tarefas grava 02/06 (ativo) ou 03/07 (desativado) no primeiro byte.</summary>
    static bool IsApproved(byte[]? data) => data == null || data.Length == 0 || (data[0] & 1) == 0;

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            var sc = sh.CreateShortcut(lnk);
            string t = sc.TargetPath;
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    static string ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : "";
        }
        var m = Regex.Match(cmd, @"^(.+?\.(exe|bat|cmd|com|lnk))(\s|$)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var first = cmd.Split(' ')[0];
        if (!first.Contains('\\'))
        {
            // ex.: "rundll32 ..." ou "ctfmon" → procura em System32
            var sys = Path.Combine(Environment.SystemDirectory, first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? first : first + ".exe");
            return File.Exists(sys) ? sys : "";
        }
        return first;
    }

    // ------------------------------------------------------------- ativar / desativar

    public static (bool ok, string? error) SetEnabled(Item it, bool on)
    {
        try
        {
            if (it.Source == Source.Task)
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
                svc.Connect();
                int slash = it.Key.LastIndexOf('\\');
                var folder = svc.GetFolder(slash <= 0 ? @"\" : it.Key[..slash]);
                var task = folder.GetTask(it.Key[(slash + 1)..]);
                task.Enabled = on;
            }
            else
            {
                var (hive, sub) = it.Source switch
                {
                    Source.RunUser => (Registry.CurrentUser, "Run"),
                    Source.RunMachine => (Registry.LocalMachine, "Run"),
                    Source.RunMachine32 => (Registry.LocalMachine, "Run32"),
                    Source.FolderUser => (Registry.CurrentUser, "StartupFolder"),
                    _ => (Registry.LocalMachine, "StartupFolder"),
                };
                using var k = hive.CreateSubKey(Approved + sub, true);
                var data = new byte[12];
                data[0] = on ? (byte)2 : (byte)3;
                if (!on) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
                k.SetValue(it.Key, data, RegistryValueKind.Binary);
            }
            it.Enabled = on;
            return (true, null);
        }
        catch (UnauthorizedAccessException) { return (false, Loc.T("precisa de administrador", "administrator required")); }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070005) { return (false, Loc.T("precisa de administrador", "administrator required")); }
        catch (Exception ex) { Paths.LogError("StartupApps.SetEnabled", ex); return (false, ex.Message); }
    }

    // ------------------------------------------------------------- tempo de boot

    /// <summary>Duração do último boot, pelo log de desempenho do Windows (precisa de administrador).</summary>
    public static (TimeSpan? total, DateTime? when) LastBoot()
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            using var e = r.ReadEvent();
            if (e == null) return (null, null);
            var ms = Data(e, "BootTime");
            return (ms is double v && v > 0 ? TimeSpan.FromMilliseconds(v) : null, e.TimeCreated);
        }
        catch { return (null, null); }
    }

    /// <summary>Evento 101: "este aplicativo demorou mais que o normal para iniciar" durante o boot.</summary>
    static void ApplySlowBoot(List<Item> list)
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=101]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            var since = DateTime.Now.AddDays(-30);
            var slow = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int n = 0; n < 200; n++)
            {
                using var e = r.ReadEvent();
                if (e == null || e.TimeCreated < since) break;
                var x = XDocument.Parse(e.ToXml());
                string? file = x.Descendants().FirstOrDefault(d => d.Name.LocalName == "Data" && (string?)d.Attribute("Name") == "Name")?.Value;
                if (Data(e, "DegradationTime") is double deg && file != null)
                    slow[Path.GetFileName(file)] = Math.Max(slow.GetValueOrDefault(Path.GetFileName(file)), deg / 1000);
            }
            foreach (var it in list)
                if (it.ExePath != "" && slow.TryGetValue(Path.GetFileName(it.ExePath), out var s)) it.SlowBootSec = s;
        }
        catch { }
    }

    static double? Data(EventRecord e, string name)
    {
        var x = XDocument.Parse(e.ToXml());
        var v = x.Descendants().FirstOrDefault(d => d.Name.LocalName == "Data" && (string?)d.Attribute("Name") == name)?.Value;
        return double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
