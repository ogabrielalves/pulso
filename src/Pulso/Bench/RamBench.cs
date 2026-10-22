using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Pulso.Core;

namespace Pulso.Bench;

/// <summary>Banda de memória (leitura, escrita, cópia), latência e teste de erros.</summary>
public static unsafe class RamBench
{
    public sealed record Result(double ReadGBs, double WriteGBs, double CopyGBs, double LatencyNs);
    public sealed record ErrorResult(long Errors, double TestedMB, int Passes);

    static long BufferBytes()
    {
        long avail = 2L << 30;
        try
        {
            var pi = new Native.PERFORMANCE_INFORMATION { cb = Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
            if (Native.GetPerformanceInfo(ref pi, pi.cb)) avail = (long)(pi.PhysicalAvailable * pi.PageSize);
        }
        catch { }
        // 256 MB por buffer, bem maior que qualquer cache L3, sem apertar a RAM do sistema.
        return Math.Clamp(avail / 8, 64L << 20, 256L << 20) & ~4095L;
    }

    public static Result Run(IProgress<(double p, string step)>? progress, CancellationToken ct)
    {
        long size = BufferBytes();
        byte* a = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        byte* b = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            new Span<byte>(a, (int)Math.Min(size, int.MaxValue)).Fill(1);
            new Span<byte>(b, (int)Math.Min(size, int.MaxValue)).Fill(2);
            int threads = Math.Max(1, Environment.ProcessorCount);

            progress?.Report((0.05, Loc.T("Leitura", "Read")));
            double read = Parallel(size, threads, TimeSpan.FromSeconds(3), ct, (off, len) => ReadSlice(a + off, len));
            progress?.Report((0.3, Loc.T("Escrita", "Write")));
            double write = Parallel(size, threads, TimeSpan.FromSeconds(3), ct, (off, len) => WriteSlice(a + off, len));
            progress?.Report((0.55, Loc.T("Cópia", "Copy")));
            double copy = Parallel(size, threads, TimeSpan.FromSeconds(3), ct, (off, len) => Buffer.MemoryCopy(a + off, b + off, len, len));
            progress?.Report((0.8, Loc.T("Latência", "Latency")));
            double lat = Latency(ct);
            progress?.Report((1, ""));
            return new Result(read, write, copy, lat);
        }
        finally
        {
            NativeMemory.AlignedFree(a);
            NativeMemory.AlignedFree(b);
        }
    }

    static long _sink;

    static void ReadSlice(byte* p, long len)
    {
        var acc0 = Vector256<long>.Zero; var acc1 = Vector256<long>.Zero; var acc2 = Vector256<long>.Zero; var acc3 = Vector256<long>.Zero;
        long* q = (long*)p;
        long n = len / 128;
        for (long i = 0; i < n; i++, q += 16)
        {
            acc0 += Vector256.Load(q);
            acc1 += Vector256.Load(q + 4);
            acc2 += Vector256.Load(q + 8);
            acc3 += Vector256.Load(q + 12);
        }
        Interlocked.Add(ref _sink, (acc0 + acc1 + acc2 + acc3).GetElement(0));
    }

    static void WriteSlice(byte* p, long len)
    {
        var v = Vector256.Create(0x5A5A5A5A5A5A5A5AL);
        long* q = (long*)p;
        long n = len / 128;
        for (long i = 0; i < n; i++, q += 16)
        {
            v.Store(q); v.Store(q + 4); v.Store(q + 8); v.Store(q + 12);
        }
    }

    /// <summary>Repete o trabalho em todas as threads pelo tempo pedido e devolve GB/s.</summary>
    static double Parallel(long size, int threads, TimeSpan dur, CancellationToken ct, Action<long, long> work)
    {
        long slice = (size / threads) & ~127L;
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        var ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            long off = t * slice;
            ts[t] = new Thread(() =>
            {
                long done = 0;
                while (sw.Elapsed < dur && !ct.IsCancellationRequested) { work(off, slice); done += slice; }
                Interlocked.Add(ref bytes, done);
            }) { IsBackground = true };
            ts[t].Start();
        }
        foreach (var t in ts) t.Join();
        ct.ThrowIfCancellationRequested();
        return bytes / sw.Elapsed.TotalSeconds / 1e9;
    }

    /// <summary>Latência: percorre uma lista encadeada aleatória de 256 MB (cada salto é uma ida à RAM).</summary>
    static double Latency(CancellationToken ct)
    {
        const int lines = (256 << 20) / 64;
        long* mem = (long*)NativeMemory.AlignedAlloc((nuint)lines * 64, 4096);
        try
        {
            var order = new int[lines];
            for (int i = 0; i < lines; i++) order[i] = i;
            var rnd = new Random(12345);
            for (int i = lines - 1; i > 0; i--) { int j = rnd.Next(i); (order[i], order[j]) = (order[j], order[i]); } // Sattolo: um único ciclo
            for (int i = 0; i < lines; i++) mem[(long)order[i] * 8] = (long)order[(i + 1) % lines] * 8;
            long idx = 0;
            for (int i = 0; i < 200_000; i++) idx = mem[idx];
            const int steps = 8_000_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) idx = mem[idx];
            sw.Stop();
            _sink += idx;
            ct.ThrowIfCancellationRequested();
            return sw.Elapsed.TotalMilliseconds * 1e6 / steps;
        }
        finally { NativeMemory.AlignedFree(mem); }
    }

    // ------------------------------------------------------------------ teste de erros

    /// <summary>
    /// Grava padrões conhecidos e confere cada palavra. Só testa a memória que o Windows
    /// consegue entregar ao programa (não substitui o MemTest86 fora do sistema).
    /// </summary>
    public static ErrorResult ErrorTest(long megabytes, int passes, IProgress<(double p, string step)>? progress, CancellationToken ct)
    {
        long bytes = megabytes << 20;
        long errors = 0;
        int threads = Math.Max(1, Environment.ProcessorCount);
        const long chunk = 64L << 20;
        var blocks = new List<nint>();
        long allocated = 0;
        try
        {
            while (allocated < bytes)
            {
                long sz = Math.Min(chunk, bytes - allocated);
                blocks.Add((nint)NativeMemory.AlignedAlloc((nuint)sz, 4096));
                allocated += sz;
            }
            ulong[] patterns = { 0, ulong.MaxValue, 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 0x0123456789ABCDEF };
            int stepsPerPass = patterns.Length + 2;
            int totalSteps = passes * stepsPerPass, step = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                foreach (var pat in patterns)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(((double)step++ / totalSteps, Loc.T($"Passada {pass + 1} · padrão {pat:X16}", $"Pass {pass + 1} · pattern {pat:X16}")));
                    errors += ForBlocks(blocks, allocated, chunk, threads, (p, n) => { Fill(p, n, pat, false); return 0; });
                    errors += ForBlocks(blocks, allocated, chunk, threads, (p, n) => Check(p, n, pat, false));
                }
                ct.ThrowIfCancellationRequested();
                progress?.Report(((double)step++ / totalSteps, Loc.T($"Passada {pass + 1} · endereço próprio", $"Pass {pass + 1} · own address")));
                errors += ForBlocks(blocks, allocated, chunk, threads, (p, n) => { Fill(p, n, 0, true); return 0; });
                errors += ForBlocks(blocks, allocated, chunk, threads, (p, n) => Check(p, n, 0, true));
                progress?.Report(((double)step++ / totalSteps, Loc.T($"Passada {pass + 1} · bits caminhando", $"Pass {pass + 1} · walking bits")));
                errors += ForBlocks(blocks, allocated, chunk, threads, (p, n) => WalkingOnes(p, n));
            }
            return new ErrorResult(errors, allocated / 1048576.0, passes);
        }
        finally
        {
            foreach (var p in blocks) NativeMemory.AlignedFree((void*)p);
        }
    }

    static long ForBlocks(List<nint> blocks, long total, long chunk, int threads, Func<nint, long, long> f)
    {
        long errors = 0;
        System.Threading.Tasks.Parallel.For(0, blocks.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            long n = Math.Min(chunk, total - i * chunk);
            Interlocked.Add(ref errors, f(blocks[i], n));
        });
        return errors;
    }

    static void Fill(nint p, long n, ulong pat, bool address)
    {
        ulong* q = (ulong*)p;
        long words = n / 8;
        if (address) for (long i = 0; i < words; i++) q[i] = (ulong)(q + i);
        else for (long i = 0; i < words; i++) q[i] = pat;
    }

    static long Check(nint p, long n, ulong pat, bool address)
    {
        ulong* q = (ulong*)p;
        long words = n / 8, err = 0;
        if (address) { for (long i = 0; i < words; i++) if (q[i] != (ulong)(q + i)) err++; }
        else for (long i = 0; i < words; i++) if (q[i] != pat) err++;
        return err;
    }

    static long WalkingOnes(nint p, long n)
    {
        ulong* q = (ulong*)p;
        long words = n / 8, err = 0;
        for (long i = 0; i < words; i++) q[i] = 1UL << (int)(i & 63);
        for (long i = 0; i < words; i++) if (q[i] != 1UL << (int)(i & 63)) err++;
        return err;
    }
}
