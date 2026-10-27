using System.IO;
using Pulso.Core;

namespace Pulso.Bench;

/// <summary>Histórico de resultados (uma linha por sessão de testes) para comparar antes/depois.</summary>
public sealed class BenchEntry
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Note { get; set; } = "";
    public double? CpuSingle { get; set; }
    public double? CpuMulti { get; set; }
    public double? Gpu3D { get; set; }
    public double? GpuFps { get; set; }
    public string? GpuRes { get; set; }
    public double? RamRead { get; set; }
    public double? RamWrite { get; set; }
    public double? RamCopy { get; set; }
    public double? RamLatency { get; set; }
    public long? RamErrors { get; set; }
    public double? RamTestedMB { get; set; }
    public string? DiskDrive { get; set; }
    public double? DiskSeqRead { get; set; }
    public double? DiskSeqWrite { get; set; }
    public double? Disk4kRead { get; set; }
    public double? Disk4kWrite { get; set; }
}

public static class BenchHistory
{
    static string File => Path.Combine(Paths.History, "benchmarks.json");
    static List<BenchEntry>? _all;

    public static List<BenchEntry> All => _all ??= Json.Load<List<BenchEntry>>(File);

    /// <summary>Linha da sessão atual (criada no primeiro teste desde que o app abriu).</summary>
    public static BenchEntry? Current { get; private set; }

    public static BenchEntry Session()
    {
        if (Current == null)
        {
            Current = new BenchEntry();
            All.Insert(0, Current);
        }
        return Current;
    }

    public static void Save()
    {
        while (All.Count > 200) All.RemoveAt(All.Count - 1);
        Json.Save(File, All);
    }

    /// <summary>Apaga todo o histórico salvo, inclusive a sessão atual.</summary>
    public static void Clear()
    {
        All.Clear();
        Current = null;
        Save();
    }

    /// <summary>Último valor anterior à sessão atual (para mostrar a variação).</summary>
    public static double? Previous(Func<BenchEntry, double?> f) => All.Where(e => e != Current).Select(f).FirstOrDefault(v => v != null);
}
