using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class JobSetBatchPlannerTests
{
    [Fact]
    public void SelectReadyJobSets_MixedSortOrder_ReturnsReadyJobSetsFromTop()
    {
        var jobSets = new[]
        {
            CreateJobSet("job-set-03", 3),
            CreateJobSet("job-set-01", 1),
            CreateJobSet("job-set-02", 2)
        };

        var ready = JobSetBatchPlanner.SelectReadyJobSets(
            jobSets,
            jobSet => jobSet.Id is "job-set-01" or "job-set-03");

        Assert.Collection(
            ready,
            first => Assert.Equal("job-set-01", first.Id),
            second => Assert.Equal("job-set-03", second.Id));
    }

    [Fact]
    public void SelectSkippedJobSets_MixedSortOrder_ReturnsSkippedJobSetsFromTop()
    {
        var jobSets = new[]
        {
            CreateJobSet("job-set-03", 3),
            CreateJobSet("job-set-01", 1),
            CreateJobSet("job-set-02", 2)
        };

        var skipped = JobSetBatchPlanner.SelectSkippedJobSets(
            jobSets,
            jobSet => jobSet.Id is "job-set-02");

        Assert.Collection(
            skipped,
            first => Assert.Equal("job-set-01", first.Id),
            second => Assert.Equal("job-set-03", second.Id));
    }

    private static JobSetSessionState CreateJobSet(string id, int sortOrder)
        => new()
        {
            Id = id,
            SortOrder = sortOrder,
            DefaultTitle = $"Job set {sortOrder}",
            OutputFolderName = $"job-set-{sortOrder:00}"
        };
}