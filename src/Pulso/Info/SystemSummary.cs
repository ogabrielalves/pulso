using Microsoft.Win32;
using Pulso.Core;

namespace Pulso.Info;

/// <summary>Resumo do hardware carregado uma vez e compartilhado (Painel, gargalo, relatório).</summary>
public static class SystemSummary
{
    public sealed class Data
    {
        public CpuInfo.Data Cpu = new();
        public List<GpuInfo.Static> Gpus = new();
        public MemoryInfo.Data Memory = new();
        public List<StorageInfo.Disk> Disks = new();
        public string OsName = "", OsVersion = "", OsBuild = "";
    }

    static Task<Data>? _task;
    static DateTime _disksTime;

    public static Task<Data> GetAsync(bool refreshDisks = false)
    {
        if (_task == null || _task.IsFaulted) _task = LoadAsync();
        else if (refreshDisks && DateTime.Now - _disksTime > TimeSpan.FromSeconds(30))
            _task = _task.ContinueWith(async t =>
            {
                var d = t.Result;
                try { d.Disks = await StorageInfo.ReadAsync(); _disksTime = DateTime.Now; } catch { }
                return d;
            }).Unwrap();
        return _task;
    }

    static async Task<Data> LoadAsync()
    {
        var d = new Data();
        try { d.Cpu = await Task.Run(CpuInfo.Get); } catch (Exception ex) { Paths.LogError("Summary.cpu", ex); }
        try { d.Gpus = await Task.Run(GpuInfo.Get); } catch (Exception ex) { Paths.LogError("Summary.gpu", ex); }
        try { d.Memory = await MemoryInfo.ReadAsync(); } catch (Exception ex) { Paths.LogError("Summary.mem", ex); }
        try { d.Disks = await StorageInfo.ReadAsync(); _disksTime = DateTime.Now; } catch (Exception ex) { Paths.LogError("Summary.disk", ex); }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = k?.GetValue("ProductName") as string ?? "Windows";
            string build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            d.OsName = product;
            d.OsVersion = k?.GetValue("DisplayVersion") as string ?? "";
            d.OsBuild = build;
        }
        catch { }
        return d;
    }
}
