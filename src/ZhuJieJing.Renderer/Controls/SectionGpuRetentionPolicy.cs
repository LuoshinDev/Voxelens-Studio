using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Keeps a bounded grace window of recently visible GPU meshes so a quick pan reversal does not re-upload them.</summary>
internal static class SectionGpuRetentionPolicy
{
    internal static int CalculateCapacity(int maximumActiveSections)
    {
        if(maximumActiveSections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActiveSections));
        return checked(maximumActiveSections + Math.Min(Math.Max(maximumActiveSections / 4, 32), 192));
    }

    internal static IReadOnlyList<SectionCoordinate> SelectEvictions(
        IReadOnlyDictionary<SectionCoordinate, long> lastUsedGeneration,
        IReadOnlySet<SectionCoordinate> active,
        int maximumActiveSections,
        IReadOnlyDictionary<SectionCoordinate, long>? allocatedBytes = null,
        long maximumBytes = 2L * 1024 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(lastUsedGeneration);
        ArgumentNullException.ThrowIfNull(active);
        int removeCount = Math.Max(0, lastUsedGeneration.Count - CalculateCapacity(maximumActiveSections));
        long bytes = allocatedBytes?.Values.Sum() ?? 0;
        if(removeCount == 0 && bytes <= maximumBytes) return [];
        var evictions = new List<SectionCoordinate>();
        foreach(var pair in lastUsedGeneration
            .Where(pair => !active.Contains(pair.Key))
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key))
        {
            if(evictions.Count >= removeCount && bytes <= maximumBytes) break;
            evictions.Add(pair.Key);
            bytes -= allocatedBytes?.GetValueOrDefault(pair.Key) ?? 0;
        }
        return evictions;
    }
}
