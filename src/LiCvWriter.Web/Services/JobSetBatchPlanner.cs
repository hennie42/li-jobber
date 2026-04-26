namespace LiCvWriter.Web.Services;

public static class JobSetBatchPlanner
{
    public static IReadOnlyList<JobSetSessionState> SelectReadyJobSets(
        IEnumerable<JobSetSessionState> jobSets,
        Func<JobSetSessionState, bool> canRun)
        => jobSets
            .OrderBy(static jobSet => jobSet.SortOrder)
            .Where(canRun)
            .ToArray();

    public static IReadOnlyList<JobSetSessionState> SelectSkippedJobSets(
        IEnumerable<JobSetSessionState> jobSets,
        Func<JobSetSessionState, bool> canRun)
        => jobSets
            .OrderBy(static jobSet => jobSet.SortOrder)
            .Where(jobSet => !canRun(jobSet))
            .ToArray();
}