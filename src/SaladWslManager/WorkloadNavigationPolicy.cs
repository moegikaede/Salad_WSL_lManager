using System;

internal struct WorkloadNavigationDecision
{
    public readonly int TargetIndex;
    public readonly bool UseLiveMode;
    public readonly bool AlignTargetRow;

    public WorkloadNavigationDecision(int targetIndex, bool useLiveMode, bool alignTargetRow)
    {
        TargetIndex = targetIndex;
        UseLiveMode = useLiveMode;
        AlignTargetRow = alignTargetRow;
    }
}

internal static class WorkloadNavigationPolicy
{
    public static WorkloadNavigationDecision Resolve(
        int rowCount,
        int selectedIndex,
        int currentIndex,
        int direction)
    {
        var maximumIndex = currentIndex < 0 ? rowCount : rowCount - 1;
        var target = Math.Max(0, Math.Min(maximumIndex, selectedIndex + direction));
        var useLiveMode = target == rowCount || target == currentIndex;
        var alignTargetRow = target >= 0 && target < rowCount && target == currentIndex;
        return new WorkloadNavigationDecision(target, useLiveMode, alignTargetRow);
    }
}
