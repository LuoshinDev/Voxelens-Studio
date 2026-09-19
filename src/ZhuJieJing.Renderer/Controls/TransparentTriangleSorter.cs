using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Exact back-to-front depth ordering with reusable storage; camera motion never comparison-sorts large vertex records.</summary>
internal sealed class TransparentTriangleSorter
{
    private const int RadixBits = 11;
    private const int BucketCount = 1 << RadixBits;
    private const int PassCount = (64 + RadixBits - 1) / RadixBits;
    private readonly int[] _histograms = new int[PassCount * BucketCount];
    private readonly TransparentTriangle[] _source;
    private readonly Vector3[] _centers;
    private readonly int[] _canonical, _first, _second;
    private readonly ulong[] _keys;

    internal TransparentTriangleSorter(TransparentTriangle[] source)
    {
        _source = source;
        _centers = source.Select(triangle => triangle.Center).ToArray();
        // Geometry normally arrives in vertex order. Do this once, including for imported/test geometry,
        // so equal depths retain the same FirstVertex tie-break as the original stable comparison sort.
        _canonical = Enumerable.Range(0, source.Length).OrderBy(index => source[index].FirstVertex).ToArray();
        _first = new int[source.Length];
        _second = new int[source.Length];
        _keys = new ulong[source.Length];
    }

    internal TransparentTriangleOrder Sort(Vector3 forward, TransparentTriangleOrder? reuse = null)
    {
        int length = _source.Length;
        Array.Copy(_canonical, _first, length);
        // All radix histograms depend only on keys, not intermediate order. Collect them once
        // while keys are hot instead of rereading keys in a different random order on every pass.
        // Six stable passes preserve all 64 depth bits while reducing scatter passes from eight.
        // Reuse histogram storage rather than adding a large stack allocation on each draw.
        Span<int> histograms = _histograms;
        histograms.Clear();
        for(int index = 0; index < length; index++)
        {
            Vector3 center = _centers[index];
            double depth = (double)center.X * forward.X + (double)center.Y * forward.Y + (double)center.Z * forward.Z;
            // Preserve Double.Compare semantics for signed zero and NaN as well as negative world coordinates.
            ulong bits = BitConverter.DoubleToUInt64Bits(depth == 0d ? 0d : depth);
            _keys[index] = double.IsNaN(depth) ? ulong.MaxValue :
                (bits & (1UL << 63)) != 0 ? bits : ~(bits ^ (1UL << 63));
            ulong key = _keys[index];
            for(int pass = 0; pass < PassCount; pass++) histograms[pass * BucketCount + (int)((key >> (pass * RadixBits)) & (BucketCount - 1))]++;
        }
        int[] current = _first, scratch = _second;
        for(int shift = 0; shift < 64; shift += RadixBits)
        {
            Span<int> counts = histograms.Slice(shift / RadixBits * BucketCount, 1 << Math.Min(RadixBits, 64 - shift));
            int offset = 0, occupied = 0;
            for(int bucket = 0; bucket < counts.Length; bucket++)
            {
                int count = counts[bucket];
                if(count != 0) occupied++;
                counts[bucket] = offset;
                offset += count;
            }
            if(occupied <= 1) continue;
            for(int index = 0; index < length; index++)
            {
                int triangle = current[index];
                scratch[counts[(int)((_keys[triangle] >> shift) & (BucketCount - 1))]++] = triangle;
            }
            (current, scratch) = (scratch, current);
        }

        TransparentTriangleOrder result = reuse is not null && reuse.Triangles.Length == length
            ? reuse : new(new TransparentTriangle[length], new uint[checked(length * 3)]);
        for(int index = 0; index < length; index++)
        {
            TransparentTriangle triangle = _source[current[index]];
            result.Triangles[index] = triangle;
            uint vertex = (uint)triangle.FirstVertex;
            result.Indices[index * 3] = vertex;
            result.Indices[index * 3 + 1] = vertex + 1;
            result.Indices[index * 3 + 2] = vertex + 2;
        }
        return result;
    }
}

internal sealed record TransparentTriangleOrder(TransparentTriangle[] Triangles, uint[] Indices);
