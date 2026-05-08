using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Numerics;

namespace RinhaFraudApi.Services;

public sealed class ReferenceStore : IDisposable
{
    public const int Dimensions = 14;
    public const int K = 5;
    private const int CellCount = 1 << 14;
    private const uint FileMagic = 0x3146_4E52u;
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly unsafe byte* _ptr;
    private readonly nuint _count;
    private readonly bool _valid;
    private readonly float[] _dimMid;
    private readonly int[] _cellOffsets;
    private int[] _cellIndices;
    private readonly int[] _neighborMasks;
    private readonly int _maxCandidates;
    public long Count => (long)_count;

    public static bool TryOpen(string path, int hammingRadius, int maxCandidates, [NotNullWhen(true)] out ReferenceStore? store)
    {
        store = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            store = new ReferenceStore(path, hammingRadius, maxCandidates);
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

    private unsafe ReferenceStore(string path, int hammingRadius, int maxCandidates)
    {
        _dimMid = new float[Dimensions];
        _cellOffsets = new int[CellCount + 1];
        _cellIndices = Array.Empty<int>();
        _neighborMasks = BuildNeighborMasks(Math.Clamp(hammingRadius, 0, Dimensions));
        _maxCandidates = Math.Max(K, maxCandidates);

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

        _cellIndices = new int[(int)_count];
        BuildSpatialIndex();
    }

    private unsafe void BuildSpatialIndex()
    {
        var rowStride = Dimensions * sizeof(float) + 1;
        var headerSize = 13u;

        ComputeMedianMids(rowStride, headerSize);

        var perCell = new int[CellCount];
        for (nuint i = 0; i < _count; i++)
        {
            var row = headerSize + i * (nuint)rowStride;
            var cid = ComputeCellId((float*)(_ptr + row));
            perCell[cid]++;
        }

        _cellOffsets[0] = 0;
        for (var c = 0; c < CellCount; c++)
        {
            _cellOffsets[c + 1] = _cellOffsets[c] + perCell[c];
        }

        var cursor = new int[CellCount];
        Array.Copy(_cellOffsets, cursor, CellCount);
        for (nuint i = 0; i < _count; i++)
        {
            var row = headerSize + i * (nuint)rowStride;
            var cid = ComputeCellId((float*)(_ptr + row));
            _cellIndices[cursor[cid]++] = (int)i;
        }
    }

    private unsafe void ComputeMedianMids(int rowStride, uint headerSize)
    {
        const int maxSamples = 65536;
        var n = (int)Math.Min((nuint)maxSamples, _count);
        var samples = new float[n];
        var step = _count / (nuint)n;
        if (step == 0)
        {
            step = 1;
        }

        for (var d = 0; d < Dimensions; d++)
        {
            for (var s = 0; s < n; s++)
            {
                var idx = (nuint)s * step;
                if (idx >= _count)
                {
                    idx = _count - 1;
                }

                var row = headerSize + idx * (nuint)rowStride;
                samples[s] = ((float*)(_ptr + row))[d];
            }

            Array.Sort(samples);
            _dimMid[d] = samples[n >> 1];
        }
    }

    private unsafe uint ComputeCellId(float* v)
    {
        uint id = 0;
        for (var d = 0; d < Dimensions; d++)
        {
            if (v[d] >= _dimMid[d])
            {
                id |= 1u << d;
            }
        }

        return id;
    }

    public bool IsValid => _valid;

    private static int[] BuildNeighborMasks(int hammingRadius)
    {
        var masks = new List<int>(CellCount);
        for (var mask = 0; mask < CellCount; mask++)
        {
            if (BitOperations.PopCount((uint)mask) <= hammingRadius)
            {
                masks.Add(mask);
            }
        }

        var result = masks.ToArray();
        Array.Sort(result, static (a, b) =>
        {
            var byBits = BitOperations.PopCount((uint)a).CompareTo(BitOperations.PopCount((uint)b));
            return byBits != 0 ? byBits : a.CompareTo(b);
        });
        return result;
    }

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

            var queryCell = ComputeCellId(q);
            var dWorst = double.PositiveInfinity;
            var checkedCandidates = 0;
            foreach (var mask in _neighborMasks)
            {
                var c = (int)(queryCell ^ (uint)mask);
                var rowBegin = _cellOffsets[c];
                var rowEnd = _cellOffsets[c + 1];
                for (var j = rowBegin; j < rowEnd; j++)
                {
                    if (checkedCandidates >= _maxCandidates && filled == K)
                    {
                        goto Done;
                    }

                    checkedCandidates++;

                    var i = (nuint)_cellIndices[j];
                    var row = headerSize + i * (nuint)rowStride;
                    var rowPtr = _ptr + row;

                    var distSq = DistanceSquared(qBlocks, fullBlocks, vn, q, rowPtr);
                    var fraud = rowPtr[(nuint)(Dimensions * sizeof(float))] != 0;

                    if (filled < K)
                    {
                        bestD[filled] = distSq;
                        isFraud[filled] = fraud;
                        filled++;
                        if (filled == K)
                        {
                            dWorst = MaxK(bestD);
                        }

                        continue;
                    }

                    if (distSq >= dWorst)
                    {
                        continue;
                    }

                    var worstSlot = FirstSlotOfMaxDistance(bestD);
                    bestD[worstSlot] = distSq;
                    isFraud[worstSlot] = fraud;
                    dWorst = MaxK(bestD);
                }
            }

        Done:
            ;
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

    private static double MaxK(ReadOnlySpan<double> bestD)
    {
        var m = bestD[0];
        for (var i = 1; i < K; i++)
        {
            if (bestD[i] > m)
            {
                m = bestD[i];
            }
        }

        return m;
    }

    private static int FirstSlotOfMaxDistance(ReadOnlySpan<double> bestD)
    {
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

        return worstIdx;
    }

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
