namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Production/regression budget values applied to one search member. The coordinator uses the plan's
/// width factors and memory budget; frozen SREQ replay uses the fixed regression values.
/// </summary>
internal static class SearchBudgetPolicy
{
    internal const int RegressionEndedKeepWidths = 8;
    internal const int RegressionEndedTriggerWidths = 16;
    internal const long RegressionMemoryBudgetBytes = 2_576_980_377;

    internal static BeamOptions Apply(BeamOptions options, int endedKeepWidths,
        int endedTriggerWidths, long memoryBudgetBytes)
    {
        int width = options.BeamWidth;
        return options with
        {
            EndedBatchTarget = checked(width * endedKeepWidths),
            EndedBatchTrigger = checked(width * endedTriggerWidths),
            StatePoolBudgetBytes = memoryBudgetBytes,
            SearchMemoryBudgetBytes = memoryBudgetBytes,
        };
    }

    internal static BeamOptions ApplyRegression(BeamOptions options)
        => Apply(options, RegressionEndedKeepWidths, RegressionEndedTriggerWidths,
            RegressionMemoryBudgetBytes);
}
