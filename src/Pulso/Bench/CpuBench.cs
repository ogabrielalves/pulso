using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pulso.Bench;

/// <summary>
/// Benchmark de CPU com três cargas que se alternam (inteiros com desvios, ponto flutuante vetorial
/// e manipulação de dados), cada uma com quantidade fixa de trabalho. Pontuação = unidades por segundo.
/// </summary>
public static class CpuBench
{
    // Fator de escala da pontuação (um núcleo moderno de desktop fica na casa dos 2.000 pontos).
    const double Scale = 8.0;

    public sealed record Result(double Score, double UnitsPerSec, int Threads);

    public static Result Run(int threads, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        var warm = TimeSpan.FromSeconds(1);
        long counted = 0;
        double countedSecs = 0;
        var ts = new Thread[threads];
        var start = new ManualResetEventSlim(false);
        long[] perThread = new long[threads * 8]; // espaçado para não disputar cache
        var deadline = warm + duration;
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            ts[t] = new Thread(() =>
            {
                var w = new Worker(id);
                start.Wait();
                while (sw.Elapsed < deadline && !ct.IsCancellationRequested)
                {
                    w.Unit();
                    if (sw.Elapsed >= warm) perThread[id * 8]++;
                }
            }) { IsBackground = true, Priority = ThreadPriority.Normal, Name = "Pulso.CpuBench" };
            ts[t].Start();
        }
        start.Set();
        while (ts.Any(t => t.IsAlive))
        {
            Thread.Sleep(100);
            progress?.Report(Math.Min(1, sw.Elapsed.TotalSeconds / deadline.TotalSeconds));
        }
        ct.ThrowIfCancellationRequested();
        for (int t = 0; t < threads; t++) counted += perThread[t * 8];
        countedSecs = Math.Max(0.001, (sw.Elapsed - warm).TotalSeconds);
        total = counted;
        double ups = total / countedSecs;
        return new Result(Math.Round(ups * Scale), ups, threads);
    }

    sealed class Worker
    {
        readonly int[] _ints = new int[4096];
        readonly byte[] _bytes = new byte[64 * 1024];
        readonly float[] _out = new float[256];
        uint _seed;
        int _phase;
        public long Sink;

        public Worker(int id) => _seed = 2463534242u + (uint)id * 7919u;

        uint Next()
        {
            uint x = _seed;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            return _seed = x;
        }

        public void Unit()
        {
            switch (_phase++ % 3)
            {
                case 0: IntegerSort(); break;
                case 1: Mandelbrot(); break;
                default: Bytes(); break;
            }
        }

        /// <summary>Ordenação + busca: muitos desvios condicionais (típico de lógica de jogo).</summary>
        void IntegerSort()
        {
            for (int i = 0; i < _ints.Length; i++) _ints[i] = (int)(Next() & 0xFFFFF);
            Array.Sort(_ints);
            long acc = 0;
            for (int i = 0; i < 2048; i++) acc += Array.BinarySearch(_ints, (int)(Next() & 0xFFFFF));
            Sink += acc;
        }

        /// <summary>Ponto flutuante vetorizado (SIMD).</summary>
        void Mandelbrot()
        {
            int w = Vector<float>.Count;
            var four = new Vector<float>(4f);
            var one = Vector<float>.One;
            Span<float> xs = stackalloc float[w];
            for (int row = 0; row < 16; row++)
            {
                float y0 = -1f + row * 0.125f;
                for (int col = 0; col < 256; col += w)
                {
                    for (int k = 0; k < w; k++) xs[k] = -2f + (col + k) * (3f / 256);
                    var cx = new Vector<float>(xs);
                    var cy = new Vector<float>(y0);
                    var zx = Vector<float>.Zero; var zy = Vector<float>.Zero; var it = Vector<float>.Zero;
                    for (int n = 0; n < 48; n++)
                    {
                        var zx2 = zx * zx; var zy2 = zy * zy;
                        var inside = Vector.LessThan(zx2 + zy2, four);
                        if (Vector.EqualsAll(inside, Vector<int>.Zero)) break;
                        it += Vector.ConditionalSelect(inside, one, Vector<float>.Zero);
                        zy = 2f * zx * zy + cy;
                        zx = zx2 - zy2 + cx;
                    }
                    _out[col] = it[0];
                }
            }
            Sink += (long)_out[17];
        }

        /// <summary>Hash e cópia de dados (parecido com compressão/descompressão).</summary>
        void Bytes()
        {
            var b = _bytes;
            uint s = Next();
            for (int i = 0; i < b.Length; i += 4) Unsafe.WriteUnaligned(ref b[i], s = s * 1664525u + 1013904223u);
            uint h = 2166136261;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            // procura de repetições (estilo LZ)
            int matches = 0;
            for (int i = 4; i < 16384; i++)
            {
                int j = (int)((h + (uint)i * 2654435761u) & 16383);
                if (b[i] == b[j] && b[i - 1] == b[(j + 16383) & 16383]) matches++;
            }
            Sink += h + matches;
        }
    }
}
