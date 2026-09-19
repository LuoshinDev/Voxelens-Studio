namespace ZhuJieJing.Core;

/// <summary>Computes the exact Section working set of one incremental construction patch.</summary>
public static class ZjjPlanSectionIndex
{
    public static IReadOnlySet<SectionCoordinate> GetTouchedSections(ZjjPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = new HashSet<SectionCoordinate>();
        foreach(PlanOperation operation in plan.Operations)
        {
            switch(operation)
            {
                case FillBoxOperation fill:
                    AddBox(
                        plan.Dimension,
                        fill.Bounds.Min,
                        fill.Bounds.MaxExclusive.Offset(-1, -1, -1),
                        result);
                    break;
                case ColumnsOperation columns:
                    foreach(BlockPosition origin in columns.Origins)
                        AddBox(plan.Dimension, origin, origin.Offset(0, columns.Height - 1, 0), result);
                    break;
            }
        }
        return result;
    }

    private static void AddBox(
        string dimension,
        BlockPosition minimum,
        BlockPosition maximumInclusive,
        HashSet<SectionCoordinate> result)
    {
        SectionCoordinate minimumSection = SectionCoordinate.FromBlock(dimension, minimum);
        SectionCoordinate maximumSection = SectionCoordinate.FromBlock(dimension, maximumInclusive);
        for(int sectionX = minimumSection.X; sectionX <= maximumSection.X; sectionX++)
        for(int sectionY = minimumSection.Y; sectionY <= maximumSection.Y; sectionY++)
        for(int sectionZ = minimumSection.Z; sectionZ <= maximumSection.Z; sectionZ++)
            result.Add(new SectionCoordinate(dimension, sectionX, sectionY, sectionZ));
    }
}
