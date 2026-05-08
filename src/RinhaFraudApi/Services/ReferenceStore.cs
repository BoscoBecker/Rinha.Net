using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace RinhaFraudApi.Services;

public sealed class ReferenceStore : IDisposable
{
    public const int Dimensions = 14;
    public const int K = 5;

    /// <summary>Magic 'R','N','F','1' read as little-endian uint32 from file bytes.</summary>
    private const uint FileMagic = 0x3146_4E52u;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly unsafe byte* _ptr;
    private readonly nuint _count;
    private readonly bool _valid;

    public long Count => (long)_count;

    public static bool TryOpen(string path, [NotNullWhen(true)] out ReferenceStore? store)
    {
        store = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            store = new ReferenceStore(path);
            if (!store._valid)
            {
                store.Dispose();
                store = null;
                return false;
            }

            return true;
        }
        catch
        {
            store?.Dispose();
            store = null;
            return false;
        }
    }

    private unsafe ReferenceStore(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read);

        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var handle = _accessor.SafeMemoryMappedViewHandle;
        byte* p = null;
        handle.AcquirePointer(ref p);
        _ptr = p;
        if (_ptr == null)
        {
            _valid = false;
            return;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_ptr, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_ptr + 4, 4));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_ptr + 8, 4));
        var dim = _ptr[12];
        if (magic != FileMagic || version != 1u || count == 0u || dim != Dimensions)
        {
            _valid = false;
            return;
        }

        _count = count;
        _valid = true;
    }

    public bool IsValid => _valid;

    public unsafe int SearchKnnFraudCount(ReadOnlySpan<float> query)
    {
        if (query.Length != Dimensions)
        {
            throw new ArgumentException("query must have 14 elements");
        }

        if (_count == 0 || !_valid)
        {
            return 0;
        }

        Span<double> bestD = stackalloc double[K];
        Span<bool> isFraud = stackalloc bool[K];
        for (var i = 0; i < K; i++)
        {
            bestD[i] = double.PositiveInfinity;
        }

        var rowStride = Dimensions * sizeof(float) + 1;
        var headerSize = 13u;

        var filled = 0;
        var vn = Vector<float>.Count;
        var fullBlocks = Dimensions / vn;
        Span<Vector<float>> qBlocks = stackalloc Vector<float>[fullBlocks];
        fixed (float* q = query)
        {
            ReadOnlySpan<float> qs = new(q, Dimensions);
            for (var b = 0; b < fullBlocks; b++)
            {
                qBlocks[b] = new Vector<float>(qs.Slice(b * vn, vn));
            }

            for (nuint i = 0; i < _count; i++)
            {
                var row = headerSize + i * (nuint)rowStride;
                var rowPtr = _ptr + row;

                if (Sse.IsSupported && i + 1 < _count)
                {
                    Sse.Prefetch0(rowPtr + rowStride);
                }

                var distSq = DistanceSquared(qBlocks, fullBlocks, vn, q, rowPtr);
                var fraud = rowPtr[(nuint)(Dimensions * sizeof(float))] != 0;

                if (filled < K)
                {
                    bestD[filled] = distSq;
                    isFraud[filled] = fraud;
                    filled++;
                    continue;
                }

                var worstIdx = 0;
                var worstVal = bestD[0];
                for (var j = 1; j < K; j++)
                {
                    if (bestD[j] > worstVal)
                    {
                        worstVal = bestD[j];
                        worstIdx = j;
                    }
                }

                if (distSq < worstVal)
                {
                    bestD[worstIdx] = distSq;
                    isFraud[worstIdx] = fraud;
                }
            }
        }

        var frauds = 0;
        var limit = filled < K ? filled : K;
        for (var i = 0; i < limit; i++)
        {
            if (isFraud[i])
            {
                frauds++;
            }
        }

        return frauds;
    }

    /// <summary>
    /// Distância euclidiana ao quadrado: blocos SIMD para dimensões alinhadas a <see cref="Vector{T}.Count"/>,
    /// cauda escalar; query pré-fatada em <paramref name="qBlocks"/> (uma vez por busca).
    /// </summary>
    private static unsafe double DistanceSquared(
        ReadOnlySpan<Vector<float>> qBlocks,
        int fullBlocks,
        int vn,
        float* q,
        byte* rowVec)
    {
        var vf = (float*)rowVec;
        double sum = 0;
        var d = 0;
        for (var b = 0; b < fullBlocks; b++)
        {
            var vv = new Vector<float>(new ReadOnlySpan<float>(vf + d, vn));
            var diff = qBlocks[b] - vv;
            sum += Vector.Sum(diff * diff);
            d += vn;
        }

        for (; d < Dimensions; d++)
        {
            var diff = q[d] - vf[d];
            sum += diff * diff;
        }

        return sum;
    }

    public void Dispose()
    {
        if (_accessor != null)
        {
            var handle = _accessor.SafeMemoryMappedViewHandle;
            if (handle != null && !handle.IsInvalid)
            {
                handle.ReleasePointer();
            }

            _accessor.Dispose();
        }

        _mmf?.Dispose();
        GC.SuppressFinalize(this);
    }
}
