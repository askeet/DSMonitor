using System.ComponentModel;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class JobTableCompatibilityTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void JobsView_RetainsStartedDescendingSortAndSearchByFolderAndFullPath()
    {
        using var viewModel = new MainViewModel(
            () => throw new InvalidOperationException("Not started in view-model tests."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5));

        var now = DateTimeOffset.UtcNow;
        var older = Job(
            "job_alpha_0001",
            @"D:\Work\Alpha Project",
            now.AddHours(-2),
            now.AddHours(-1));
        var newer = Job(
            "job_beta_0002",
            @"D:\Work\Beta Project",
            now.AddHours(-1),
            now.AddMinutes(-10));

        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = now,
            State = MonitoringState.Idle,
            ForgeDirectoryAvailable = true,
            Jobs = new[] { older, newer },
        });

        CollectionAssert.AreEqual(
            new[] { "job_beta_0002", "job_alpha_0001" },
            viewModel.JobsView.Cast<JobRowViewModel>().Select(row => row.JobId).ToArray());

        viewModel.SearchText = "Alpha Project";
        CollectionAssert.AreEqual(
            new[] { "job_alpha_0001" },
            viewModel.JobsView.Cast<JobRowViewModel>().Select(row => row.JobId).ToArray());

        viewModel.SearchText = @"D:\Work\Beta Project";
        CollectionAssert.AreEqual(
            new[] { "job_beta_0002" },
            viewModel.JobsView.Cast<JobRowViewModel>().Select(row => row.JobId).ToArray());

        viewModel.SearchText = "job_alpha";
        CollectionAssert.AreEqual(
            new[] { "job_alpha_0001" },
            viewModel.JobsView.Cast<JobRowViewModel>().Select(row => row.JobId).ToArray());
    }

    [TestMethod]
    public void JobsView_SortsDurationNumericallyIncludingMissingValues()
    {
        using var viewModel = new MainViewModel(
            () => throw new InvalidOperationException("Not started in view-model tests."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5));

        var now = DateTimeOffset.UtcNow;
        var shortJob = Job("job_short", @"D:\Work\Short", now.AddHours(-1), now.AddSeconds(-3598));
        var longJob = Job("job_long", @"D:\Work\Long", now.AddHours(-1), now.AddSeconds(-3590));
        var missing = Job("job_missing", @"D:\Work\Missing", now.AddHours(-1), null);
        var reversed = Job("job_reversed", @"D:\Work\Reversed", now.AddHours(-1), now.AddHours(-2));

        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = now,
            State = MonitoringState.Idle,
            ForgeDirectoryAvailable = true,
            Jobs = new[] { longJob, missing, shortJob, reversed }
        });

        viewModel.JobsView.SortDescriptions.Clear();
        viewModel.JobsView.SortDescriptions.Add(
            new SortDescription(
                nameof(JobRowViewModel.DurationSortSeconds),
                ListSortDirection.Ascending));

        var ordered = viewModel.JobsView.Cast<JobRowViewModel>().ToArray();
        Assert.AreEqual(4, ordered.Length);
        Assert.AreEqual("job_short", ordered[0].JobId);
        Assert.AreEqual("job_long", ordered[1].JobId);
        CollectionAssert.AreEquivalent(
            new[] { "job_missing", "job_reversed" },
            ordered.Skip(2).Select(row => row.JobId).ToArray());
    }

    private static ForgeJobSnapshot Job(
        string jobId,
        string directory,
        DateTimeOffset started,
        DateTimeOffset? lastActivity) =>
        new()
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = jobId,
                MetadataPath = @"C:\synthetic\runs\" + jobId + ".json",
                Directory = directory,
                Started = started
            },
            Status = ForgeJobStatus.Done,
            LastActivity = lastActivity,
            ObservedAt = started.AddHours(1)
        };
}
