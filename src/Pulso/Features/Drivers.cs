using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Drivers: inventário (WMI), dispositivos com problema e atualização segura.
/// A única fonte de drivers é o Windows Update (pacotes assinados enviados pelos fabricantes);
/// antes de instalar cria ponto de restauração e exporta o driver atual, e nunca oferece versão mais antiga.
/// </summary>
public static partial class Drivers
{
    public sealed class Installed
    {
        public string DeviceId = "", Name = "", Class = "", Provider = "", Manufacturer = "", Version = "", Inf = "";
        public DateTime? Date;
        public bool Signed;
        public string[] HardwareIds = Array.Empty<string>();
        public bool IsMicrosoft => Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        public bool IsOem => Inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Problem
    {
        public string DeviceId = "", Name = "";
        public uint Code;
        public string Description => Describe(Code);
    }

    public enum Offer { Newer, Same, Older }

    public sealed class Update
    {
        internal dynamic Com = null!;
        public string Id = "", Title = "", Class = "", Provider = "", Model = "", HardwareId = "";
        public string? Version;
        public DateTime? Date;
        public double Size;
        public Installed? Current;
        public Offer Offer;
        public string DisplayName => Current?.Name is { Length: > 0 } n ? n : Model.Length > 0 ? Model : Title;
    }

    public sealed class HistoryEntry
    {
        public DateTime Date { get; set; }
        public string Device { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string OldVersion { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public string? BackupDir { get; set; }
        public bool Ok { get; set; }
        public bool Reboot { get; set; }
        public string? Error { get; set; }
    }

    // ------------------------------------------------------------- inventário

    public static List<Installed> ReadInstalled()
    {
        var ids = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity");
            foreach (ManagementObject o in s.Get())
                if (o["DeviceID"] is string id)
                    ids[id] = (o["HardwareID"] as string[] ?? Array.Empty<string>()).Concat(o["CompatibleID"] as string[] ?? Array.Empty<string>()).ToArray();
        }
        catch (Exception ex) { Paths.LogError("Drivers.PnPEntity", ex); }

        var list = new List<Installed>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, Manufacturer, DriverVersion, DriverDate, IsSigned, InfName FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get())
            {
                var d = new Installed
                {
                    DeviceId = o["DeviceID"] as string ?? "",
                    Name = (o["DeviceName"] as string ?? "").Trim(),
                    Class = o["DeviceClass"] as string ?? "",
                    Provider = (o["DriverProviderName"] as string ?? "").Trim(),
                    Manufacturer = (o["Manufacturer"] as string ?? "").Trim(),
                    Version = o["DriverVersion"] as string ?? "",
                    Inf = o["InfName"] as string ?? "",
                    Signed = o["IsSigned"] as bool? ?? false,
                };
                if (o["DriverDate"] is string dd && dd.Length >= 8)
                    try { d.Date = ManagementDateTimeConverter.ToDateTime(dd); } catch { }
                if (d.Name == "" || d.Version == "") continue;
                if (ids.TryGetValue(d.DeviceId, out var h)) d.HardwareIds = h;
                list.Add(d);
            }
        }
        catch (Exception ex) { Paths.LogError("Drivers.PnPSignedDriver", ex); }
        return list.OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
    }

    public static List<Problem> ReadProblems()
    {
        var list = new List<Problem>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID, Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (ManagementObject o in s.Get())
            {
                uint code = Convert.ToUInt32(o["ConfigManagerErrorCode"] ?? 0u);
                if (code == 45) continue; // dispositivo desconectado: não é problema
                list.Add(new Problem { DeviceId = o["DeviceID"] as string ?? "", Name = o["Name"] as string ?? Loc.T("Dispositivo desconhecido", "Unknown device"), Code = code });
            }
        }
        catch (Exception ex) { Paths.LogError("Drivers.Problems", ex); }
        return list;
    }

    static string Describe(uint code) => code switch
    {
        1 => Loc.T("Não configurado corretamente", "Not configured correctly"),
        3 => Loc.T("Driver corrompido ou sem memória", "Driver corrupted or out of memory"),
        10 => Loc.T("O dispositivo não consegue iniciar", "The device cannot start"),
        12 => Loc.T("Sem recursos livres (conflito)", "Not enough free resources (conflict)"),
        14 => Loc.T("Precisa reiniciar o Windows", "Windows restart required"),
        18 => Loc.T("Reinstale os drivers deste dispositivo", "Reinstall the drivers for this device"),
        19 => Loc.T("Configuração no registro está corrompida", "Registry configuration is corrupted"),
        21 => Loc.T("Windows está removendo o dispositivo", "Windows is removing the device"),
        22 => Loc.T("Desativado", "Disabled"),
        24 => Loc.T("Não presente ou com driver incompleto", "Not present or driver incomplete"),
        28 => Loc.T("Driver não instalado", "Driver not installed"),
        29 => Loc.T("Desativado pelo firmware", "Disabled by firmware"),
        31 => Loc.T("O Windows não conseguiu carregar o driver", "Windows could not load the driver"),
        32 => Loc.T("Serviço do driver desativado", "Driver service disabled"),
        37 => Loc.T("O driver falhou ao iniciar", "The driver failed to initialize"),
        39 => Loc.T("Driver corrompido ou ausente", "Driver corrupted or missing"),
        43 => Loc.T("O Windows parou o dispositivo após ele relatar problemas", "Windows stopped the device after it reported problems"),
        48 => Loc.T("Driver bloqueado por incompatibilidade", "Driver blocked for incompatibility"),
        52 => Loc.T("Assinatura digital do driver não verificada", "Driver digital signature could not be verified"),
        _ => Loc.T($"Código de erro {code}", $"Error code {code}"),
    };

    /// <summary>Avisos de ambiente: serviço do Windows Update desativado, WSUS, política que exclui drivers.</summary>
    public static List<string> Warnings()
    {
        var notes = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wuauserv", "Start", null) is int st && st == 4)
                notes.Add(Loc.T("O serviço Windows Update está desativado. A busca por drivers não vai funcionar até ele ser reativado.", "The Windows Update service is disabled. Driver search will not work until it is re-enabled."));
            const string pol = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
            if (Registry.GetValue(pol + @"\AU", "UseWUServer", null) is int ws && ws == 1)
                notes.Add(Loc.T("Este PC é gerenciado por um servidor WSUS (empresa/escola). A busca pode vir vazia ou ser bloqueada pela política.", "This PC is managed by a WSUS server (work/school). The search may come back empty or be blocked by policy."));
            if (Registry.GetValue(pol, "ExcludeWUDriversInQualityUpdate", null) is int ex && ex == 1)
                notes.Add(Loc.T("Uma política do Windows exclui drivers das atualizações. A lista pode vir vazia.", "A Windows policy excludes drivers from updates. The list may be empty."));
        }
        catch { }
        return notes;
    }

    // ------------------------------------------------------------- Windows Update

    static dynamic NewCom(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)!;

    static dynamic? _session;
    static dynamic Session
    {
        get
        {
            if (_session != null) return _session;
            _session = NewCom("Microsoft.Update.Session");
            _session.ClientApplicationID = "Pulso";
            return _session;
        }
    }

    /// <summary>Procura drivers no Windows Update e classifica cada um contra o driver instalado. Demora de segundos a minutos.</summary>
    public static List<Update> Search(List<Installed> installed)
    {
        var searcher = Session.CreateUpdateSearcher();
        searcher.ServerSelection = 2; // ssWindowsUpdate: direto da Microsoft, mesmo com WSUS configurado
        searcher.Online = true;
        var result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        var list = new List<Update>();
        var updates = result.Updates;
        int n = updates.Count;
        for (int i = 0; i < n; i++)
        {
            var u = updates[i];
            var up = new Update { Com = u };
            try
            {
                up.Id = (string)u.Identity.UpdateID;
                up.Title = (string)u.Title ?? "";
                up.Class = Str(() => u.DriverClass);
                up.Provider = Str(() => u.DriverProvider);
                up.Model = Str(() => u.DriverModel);
                up.HardwareId = Str(() => u.DriverHardwareID);
                try { up.Date = (DateTime)u.DriverVerDate; } catch { }
                try { up.Size = Convert.ToDouble(u.MaxDownloadSize); } catch { }
            }
            catch (Exception ex) { Paths.LogError("Drivers.Search item", ex); continue; }
            up.Version = VersionFromTitle(up.Title);
            up.Current = installed.FirstOrDefault(d => d.HardwareIds.Any(h => h.Equals(up.HardwareId, StringComparison.OrdinalIgnoreCase)));
            up.Offer = Classify(up);
            list.Add(up);
        }
        return list;
    }

    static string Str(Func<object?> f)
    {
        try { return f() as string ?? ""; } catch { return ""; }
    }

    // Títulos do Windows Update terminam na versão: "Intel - System - 1.2.3.4" ou "... Driver Update (1.2.3.4)"
    static string? VersionFromTitle(string title)
    {
        var m = Regex.Match(title, @"(\d+(?:\.\d+){1,3})\)?\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Evita "atualização" para versão mais antiga, problema conhecido do Windows Update com Intel e Realtek.</summary>
    static Offer Classify(Update u)
    {
        if (u.Current == null) return Offer.Newer;
        if (u.Version != null && Version.TryParse(u.Version, out var nv) && Version.TryParse(u.Current.Version, out var cv))
            return nv > cv ? Offer.Newer : nv == cv ? Offer.Same : Offer.Older;
        if (u.Date is DateTime d && u.Current.Date is DateTime c)
            return d.Date > c.Date ? Offer.Newer : d.Date == c.Date ? Offer.Same : Offer.Older;
        return Offer.Newer;
    }

    /// <summary>Exporta o driver atual (só pacotes de terceiros, "oemNN.inf"; os da caixa do Windows não somem).</summary>
    public static string? Backup(Installed? d)
    {
        if (d == null || !d.IsOem) return null;
        var dir = Path.Combine(Paths.AppData, "drivers-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Path.GetFileNameWithoutExtension(d.Inf));
        Directory.CreateDirectory(dir);
        var (code, output) = Run("pnputil.exe", $"/export-driver {d.Inf} \"{dir}\"");
        if (code != 0 || !Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Any())
        {
            Paths.LogError("Drivers.Backup", new Exception($"pnputil {code}: {output}"));
            try { Directory.Delete(dir, true); } catch { }
            return null;
        }
        return dir;
    }

    /// <summary>Baixa e instala um driver. Retorna (ok, reiniciar, erro).</summary>
    public static (bool ok, bool reboot, string? error) Install(Update u)
    {
        try
        {
            if (!(bool)u.Com.EulaAccepted) u.Com.AcceptEula();
            var coll = NewCom("Microsoft.Update.UpdateColl");
            coll.Add(u.Com);
            var dl = Session.CreateUpdateDownloader();
            dl.Updates = coll;
            var dr = dl.Download();
            if ((int)dr.ResultCode is not (2 or 3)) return (false, false, ResultText((int)dr.ResultCode, (int)dr.HResult));
            var inst = Session.CreateUpdateInstaller();
            inst.Updates = coll;
            inst.AllowSourcePrompts = false;
            var ir = inst.Install();
            var one = ir.GetUpdateResult(0);
            int rc = (int)one.ResultCode;
            return (rc is 2 or 3, (bool)ir.RebootRequired, rc is 2 or 3 ? null : ResultText(rc, (int)one.HResult));
        }
        catch (COMException ex) { return (false, false, Hr(ex.HResult)); }
        catch (Exception ex) { Paths.LogError("Drivers.Install", ex); return (false, false, ex.Message); }
    }

    static string ResultText(int code, int hr) => code switch
    {
        4 => Loc.T("falhou", "failed") + (hr != 0 ? " · " + Hr(hr) : ""),
        5 => Loc.T("cancelado", "aborted"),
        _ => Hr(hr),
    };

    public static string Hr(int hr) => (uint)hr switch
    {
        0x80240044 or 0x80070005 => Loc.T("precisa de administrador", "administrator required"),
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x8024401C => Loc.T("sem conexão com o Windows Update", "cannot reach Windows Update"),
        0x8024A000 or 0x80240016 => Loc.T("o Windows Update está ocupado com outra instalação", "Windows Update is busy with another installation"),
        0x80070422 => Loc.T("serviço Windows Update desativado", "Windows Update service disabled"),
        _ => $"0x{(uint)hr:X8}",
    };

    // ------------------------------------------------------------- ponto de restauração

    public enum RestoreResult { Created, Failed }

    /// <summary>
    /// Cria um ponto de restauração. O Windows só cria um a cada 24 h; se já existe um recente, isso conta como sucesso.
    /// </summary>
    public static (RestoreResult result, string detail) CreateRestorePoint(string description)
    {
        try
        {
            var before = LatestRestorePoint();
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var p = cls.GetMethodParameters("CreateRestorePoint");
            p["Description"] = description;
            p["RestorePointType"] = 10;  // DEVICE_DRIVER_INSTALL
            p["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
            using var r = cls.InvokeMethod("CreateRestorePoint", p, null);
            uint rv = Convert.ToUInt32(r?["ReturnValue"] ?? 1u);
            var after = LatestRestorePoint();
            if (rv == 0 && after is DateTime a && (DateTime.Now - a).TotalHours < 24)
                return (RestoreResult.Created, after != before ? Loc.T("ponto de restauração criado", "restore point created")
                                                                : Loc.T($"usando o ponto de restauração de {a.ToString("g", Fmt.Culture)}", $"using the restore point from {a.ToString("g", Fmt.Culture)}"));
            return (RestoreResult.Failed, rv == 1058 || rv == 0x80070422
                ? Loc.T("a Proteção do Sistema está desativada", "System Protection is turned off")
                : Loc.T($"código {rv}", $"code {rv}"));
        }
        catch (Exception ex)
        {
            Paths.LogError("Drivers.RestorePoint", ex);
            return (RestoreResult.Failed, Loc.T("a Proteção do Sistema está desativada ou indisponível", "System Protection is off or unavailable"));
        }
    }

    static DateTime? LatestRestorePoint()
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT CreationTime FROM SystemRestore"));
            DateTime? best = null;
            foreach (ManagementObject o in s.Get())
                if (o["CreationTime"] is string ct)
                {
                    var t = ManagementDateTimeConverter.ToDateTime(ct);
                    if (best == null || t > best) best = t;
                }
            return best;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------- reverter

    /// <summary>Volta ao driver anterior do dispositivo, igual ao "Reverter driver" do Gerenciador de Dispositivos.</summary>
    public static (bool ok, bool reboot, string? error) Rollback(string deviceId)
    {
        nint set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) return (false, false, $"0x{Marshal.GetLastPInvokeError():X8}");
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfo(set, deviceId, 0, 0, ref data))
                return (false, false, Loc.T("dispositivo não encontrado", "device not found"));
            if (!DiRollbackDriver(set, ref data, 0, 1 /* ROLLBACK_FLAG_NO_UI */, out bool reboot))
            {
                int err = Marshal.GetLastPInvokeError();
                return (false, false, (uint)err == 0xE0000203 || err == 2 ? Loc.T("o Windows não guardou um driver anterior para este dispositivo", "Windows has no previous driver saved for this device") : $"0x{err:X8}");
            }
            return (true, reboot, null);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Reinstala o driver exportado antes da atualização, forçando-o mesmo sendo mais antigo.</summary>
    public static (bool ok, bool reboot, string? error) RestoreBackup(HistoryEntry h)
    {
        if (h.BackupDir == null || !Directory.Exists(h.BackupDir)) return (false, false, Loc.T("backup não encontrado", "backup not found"));
        var inf = Directory.EnumerateFiles(h.BackupDir, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (inf == null) return (false, false, Loc.T("backup sem arquivo .inf", "backup has no .inf file"));
        if (UpdateDriverForPlugAndPlayDevices(0, h.HardwareId, inf, 1 /* INSTALLFLAG_FORCE */, out bool reboot)) return (true, reboot, null);
        int err = Marshal.GetLastPInvokeError();
        return (false, false, (uint)err == 0xE000020B ? Loc.T("o dispositivo não está conectado", "the device is not connected") : $"0x{err:X8}");
    }

    // ------------------------------------------------------------- histórico

    static string HistoryFile => Path.Combine(Paths.AppData, "drivers.json");

    public static List<HistoryEntry> History() => Json.Load<List<HistoryEntry>>(HistoryFile);

    public static void AddHistory(HistoryEntry e)
    {
        var list = History();
        list.Insert(0, e);
        Json.Save(HistoryFile, list.Take(50).ToList());
    }

    static (int code, string output) Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return (-1, "");
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            return (p.ExitCode, o.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Página oficial de drivers do fabricante da GPU.</summary>
    public static string? GpuVendorUrl(string provider, string name)
    {
        string s = provider + " " + name;
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "https://www.nvidia.com/pt-br/drivers/";
        if (s.Contains("AMD", StringComparison.OrdinalIgnoreCase) || s.Contains("ATI ", StringComparison.OrdinalIgnoreCase) || s.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "https://www.amd.com/pt/support/download/drivers.html";
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "https://www.intel.com.br/content/www/br/pt/support/detect.html";
        return null;
    }

    // ------------------------------------------------------------- SetupAPI

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwnd);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfo(nint set, string instanceId, nint hwnd, uint flags, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiRollbackDriver(nint set, ref SP_DEVINFO_DATA data, nint hwnd, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);

    [LibraryImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateDriverForPlugAndPlayDevices(nint hwnd, string hardwareId, string infPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
