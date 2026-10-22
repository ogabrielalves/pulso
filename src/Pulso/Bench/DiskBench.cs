using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulso.Core;

namespace Pulso.Bench;

/// <summary>
/// Leitura/escrita sequencial (1 MiB, fila 8) e aleatória 4K (fila 1), sem cache do Windows
/// (FILE_FLAG_NO_BUFFERING), como o CrystalDiskMark.
/// </summary>
public static class DiskBench
{
    public sealed record Result(double SeqReadMBs, double SeqWriteMBs, double Rnd4kReadMBs, double Rnd4kWriteMBs, double Rnd4kReadIops, string Drive);

    const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static string TestFilePath(string driveRoot)
    {
        string root = Path.GetPathRoot(driveRoot) ?? driveRoot;
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        // Na unidade do Windows a raiz é protegida: usa a pasta temporária (mesmo disco).
        if (string.Equals(root, sysRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetTempPath(), "PulsoBench.tmp");
        return Path.Combine(root, "PulsoBench.tmp");
    }

    public static async Task<Result> RunAsync(string driveRoot, long fileMB, IProgress<(double p, string step)>? progress, CancellationToken ct)
    {
        string path = TestFilePath(driveRoot);
        long size = fileMB << 20;
        var drive = new DriveInfo(driveRoot);
        if (drive.AvailableFreeSpace < size * 2)
            throw new IOException(Loc.T("Espaço livre insuficiente na unidade para o teste.", "Not enough free space on the drive for the test."));

        const int seqBlock = 1 << 20, rndBlock = 4096;
        using var mgr = new NativeBuffer(seqBlock * 8);
        try
        {
            // dados aleatórios (incompressíveis)
            new Random(7).NextBytes(mgr.GetSpan());

            double seqW, seqR, rR, rW, iops;
            using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | NoBuffering | FileOptions.WriteThrough | FileOptions.DeleteOnClose, size))
            {
                progress?.Report((0.02, Loc.T("Escrita sequencial", "Sequential write")));
                seqW = await Sequential(h, size, mgr.Memory, seqBlock, 8, write: true, TimeSpan.FromSeconds(8), ct);
                // garante o arquivo inteiro gravado para os testes de leitura
                await Sequential(h, size, mgr.Memory, seqBlock, 8, write: true, TimeSpan.FromSeconds(60), ct, onlyUnwritten: true);
                progress?.Report((0.3, Loc.T("Leitura sequencial", "Sequential read")));
                seqR = await Sequential(h, size, mgr.Memory, seqBlock, 8, write: false, TimeSpan.FromSeconds(6), ct);
                progress?.Report((0.55, Loc.T("Leitura aleatória 4K", "4K random read")));
                (rR, iops) = await Random4K(h, size, mgr.Memory, rndBlock, 1, write: false, TimeSpan.FromSeconds(6), ct);
                progress?.Report((0.8, Loc.T("Escrita aleatória 4K", "4K random write")));
                (rW, _) = await Random4K(h, size, mgr.Memory, rndBlock, 1, write: true, TimeSpan.FromSeconds(6), ct);
            }
            progress?.Report((1, ""));
            return new Result(seqR, seqW, rR, rW, iops, driveRoot);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    static long _written;

    static async Task<double> Sequential(SafeFileHandle h, long size, Memory<byte> mem, int block, int queue, bool write, TimeSpan limit, CancellationToken ct, bool onlyUnwritten = false)
    {
        long next = onlyUnwritten ? _written : 0;
        if (!onlyUnwritten && write) _written = 0;
        if (onlyUnwritten && next >= size) return 0;
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        async Task Worker(int slot)
        {
            var slice = mem.Slice(slot * block, block);
            while (sw.Elapsed < limit && !ct.IsCancellationRequested)
            {
                long off = Interlocked.Add(ref next, block) - block;
                if (off + block > size) break;
                if (write) await RandomAccess.WriteAsync(h, slice, off, ct);
                else await RandomAccess.ReadAsync(h, slice, off, ct);
                Interlocked.Add(ref bytes, block);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, queue).Select(Worker));
        ct.ThrowIfCancellationRequested();
        if (write) _written = Math.Max(_written, Math.Min(next, size));
        return bytes / sw.Elapsed.TotalSeconds / 1e6;
    }

    static async Task<(double mbs, double iops)> Random4K(SafeFileHandle h, long size, Memory<byte> mem, int block, int queue, bool write, TimeSpan limit, CancellationToken ct)
    {
        long ops = 0;
        long blocks = size / block;
        var sw = Stopwatch.StartNew();
        async Task Worker(int slot)
        {
            var rnd = new Random(slot * 31 + 1);
            var slice = mem.Slice(slot * block, block);
            while (sw.Elapsed < limit && !ct.IsCancellationRequested)
            {
                long off = rnd.NextInt64(blocks) * block;
                if (write) await RandomAccess.WriteAsync(h, slice, off, ct);
                else await RandomAccess.ReadAsync(h, slice, off, ct);
                Interlocked.Increment(ref ops);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, queue).Select(Worker));
        ct.ThrowIfCancellationRequested();
        double secs = sw.Elapsed.TotalSeconds;
        return (ops * (double)block / secs / 1e6, ops / secs);
    }

    /// <summary>Memory&lt;byte&gt; sobre memória nativa alinhada (exigência do acesso sem cache).</summary>
    sealed unsafe class NativeBuffer : MemoryManager<byte>
    {
        byte* _p;
        readonly int _length;

        public NativeBuffer(int length)
        {
            _length = length;
            _p = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
        }

        public override Span<byte> GetSpan() => new(_p, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new(_p + elementIndex);
        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            if (_p != null) NativeMemory.AlignedFree(_p);
            _p = null;
        }
    }

    public static List<(string root, string label)> Drives()
    {
        var list = new List<(string, string)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? Loc.T("Disco local", "Local disk") : d.VolumeLabel;
                list.Add((d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')} {label} · {Fmt.Bytes(d.AvailableFreeSpace)} {Loc.T("livres", "free")}"));
            }
            catch { }
        }
        return list;
    }
}
