using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Numerics;

namespace RinhaFraudApi.Services;

public sealed class ReferenceStore : IDisposable
{
    public const int Dimensions = 14;
    public const int K = 5;

    /// <summary>Número de células do grid: 2^14 (uma bit por dimensão).</summary>
    private const int CellCount = 1 << 14;

    /// <summary>Magic 'R','N','F','1' read as little-endian uint32 from file bytes.</summary>
    private const uint FileMagic = 0x3146_4E52u;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly unsafe byte* _ptr;
    private readonly nuint _count;
    private readonly bool _valid;

    private readonly float[] _dimMin;
    private readonly float[] _dimMax;
    private readonly float[] _dimMid;

    /// <summary>Limites [cell * 14 + d] da caixa AABB da célula no espaço normalizado (poda exata).</summary>
    private readonly float[] _cellLo;

    private readonly float[] _cellHi;

    /// <summary>Prefixo: length CellCount+1; pontos da célula c estão em [_cellIndices[offsets[c]], offsets[c+1]).</summary>
    private readonly int[] _cellOffsets;

    private int[] _cellIndices;

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
        _dimMin = new float[Dimensions];
        _dimMax = new float[Dimensions];
        _dimMid = new float[Dimensions];
        _cellLo = new float[CellCount * Dimensions];
        _cellHi = new float[CellCount * Dimensions];
        _cellOffsets = new int[CellCount + 1];
        _cellIndices = Array.Empty<int>();

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

        for (var d = 0; d < Dimensions; d++)
        {
            _dimMin[d] = float.PositiveInfinity;
            _dimMax[d] = float.NegativeInfinity;
        }

        for (nuint i = 0; i < _count; i++)
        {
            var row = headerSize + i * (nuint)rowStride;
            var vf = (float*)(_ptr + row);
            for (var d = 0; d < Dimensions; d++)
            {
                var v = vf[d];
                if (v < _dimMin[d])
                {
                    _dimMin[d] = v;
                }

                if (v > _dimMax[d])
                {
                    _dimMax[d] = v;
                }
            }
        }

        for (var d = 0; d < Dimensions; d++)
        {
            if (_dimMax[d] - _dimMin[d] < 1e-9f)
            {
                _dimMin[d] -= 1e-6f;
                _dimMax[d] += 1e-6f;
            }
        }

        ComputeMedianMids(rowStride, headerSize);
        FillCellBoundingBoxes();

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

    /// <summary>
    /// Separação por mediana amostrada (melhor que min+max/2 em dados enviesados, mantém 2^14 células).
    /// </summary>
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

    private void FillCellBoundingBoxes()
    {
        for (var c = 0; c < CellCount; c++)
        {
            var b = c * Dimensions;
            for (var d = 0; d < Dimensions; d++)
            {
                var upper = ((uint)c & (1u << d)) != 0;
                _cellLo[b + d] = upper ? _dimMid[d] : _dimMin[d];
                _cellHi[b + d] = upper ? _dimMax[d] : _dimMid[d];
            }
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

    /// <summary>
    /// Distância ao quadrado mínima do ponto <paramref name="q"/> à caixa da célula (AABB pré-computada).
    /// </summary>
    private unsafe double MinDistSqPointToCell(float* q, int cellId)
    {
        var b = cellId * Dimensions;
        var vn = Vector<float>.Count;
        double sum = 0;
        var d = 0;
        for (; d + vn <= Dimensions; d += vn)
        {
            var qv = new Vector<float>(new ReadOnlySpan<float>(q + d, vn));
            var lov = new Vector<float>(_cellLo.AsSpan(b + d, vn));
            var hiv = new Vector<float>(_cellHi.AsSpan(b + d, vn));
            var below = Vector.LessThan(qv, lov);
            var above = Vector.GreaterThan(qv, hiv);
            var t = Vector.ConditionalSelect(below, lov - qv, Vector.ConditionalSelect(above, qv - hiv, Vector<float>.Zero));
            sum += Vector.Sum(t * t);
        }

        for (; d < Dimensions; d++)
        {
            var lo = _cellLo[b + d];
            var hi = _cellHi[b + d];
            var qd = q[d];
            if (qd < lo)
            {
                var t = lo - qd;
                sum += t * t;
            }
            else if (qd > hi)
            {
                var t = qd - hi;
                sum += t * t;
            }
        }

        return sum;
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

        var activeCells = 0;
        for (var c = 0; c < CellCount; c++)
        {
            if (_cellOffsets[c + 1] > _cellOffsets[c])
            {
                activeCells++;
            }
        }

        var orderDist = ArrayPool<double>.Shared.Rent(activeCells);
        var orderCell = ArrayPool<int>.Shared.Rent(activeCells);
        try
        {
            var o = 0;
            fixed (float* q = query)
            {
                ReadOnlySpan<float> qs = new(q, Dimensions);
                for (var b = 0; b < fullBlocks; b++)
                {
                    qBlocks[b] = new Vector<float>(qs.Slice(b * vn, vn));
                }

                for (var c = 0; c < CellCount; c++)
                {
                    var start = _cellOffsets[c];
                    var end = _cellOffsets[c + 1];
                    if (start >= end)
                    {
                        continue;
                    }

                    orderDist[o] = MinDistSqPointToCell(q, c);
                    orderCell[o] = c;
                    o++;
                }

                Array.Sort(orderDist, orderCell, 0, activeCells);

                var dWorst = double.PositiveInfinity;
                for (var ci = 0; ci < activeCells; ci++)
                {
                    var cellLo = orderDist[ci];
                    if (filled == K && cellLo > dWorst)
                    {
                        break;
                    }

                    var c = orderCell[ci];
                    var rowBegin = _cellOffsets[c];
                    var rowEnd = _cellOffsets[c + 1];
                    for (var j = rowBegin; j < rowEnd; j++)
                    {
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
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(orderDist);
            ArrayPool<int>.Shared.Return(orderCell);
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

    /// <summary>Mesmo critério do scan linear: índice do maior valor em bestD[0..k-1] com empate → menor índice.</summary>
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
