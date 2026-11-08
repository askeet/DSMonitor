using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class MainViewModelTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void ApplySnapshot_ShowsIdleWithUnknownCountAndSelectsJob()
    {
        using var viewModel = CreateViewModel();
        var snapshot = SnapshotWithJob(ForgeJobStatus.Unknown, Array.Empty<MonitoringEvent>());

        viewModel.ApplySnapshot(snapshot);

        Assert.AreEqual(MonitoringState.Idle, viewModel.MonitoringState);
        Assert.AreEqual("IDLE", viewModel.MonitoringStateText);
        Assert.AreEqual(1, viewModel.TotalCount);
        Assert.AreEqual(1, viewModel.UnknownCount);
        Assert.AreEqual(0, viewModel.ActiveCount);
        StringAssert.Contains(viewModel.UnknownDetailText, "unknown");
        StringAssert.Contains(viewModel.StateDetail, "unknown");
        Assert.AreEqual(1, viewModel.Jobs.Count);
        Assert.IsNotNull(viewModel.SelectedJob);
        Assert.AreEqual("job_vm_0001", viewModel.SelectedJob!.JobId);
    }

    [TestMethod]
    public void ApplySnapshot_ExplicitRunning_UpdatesMonitoringState()
    {
        using var viewModel = CreateViewModel();

        viewModel.ApplySnapshot(SnapshotWithJob(ForgeJobStatus.Running, Array.Empty<MonitoringEvent>()));

        Assert.AreEqual(MonitoringState.Monitoring, viewModel.MonitoringState);
        Assert.AreEqual("MONITORING", viewModel.MonitoringStateText);
        Assert.AreEqual(1, viewModel.ActiveCount);
        Assert.AreEqual(0, viewModel.UnknownCount);
    }

    [TestMethod]
    public void ApplySnapshot_Repeatedly_DoesNotDuplicateTimelineEvents()
    {
        using var viewModel = CreateViewModel();
        var events = new[]
        {
            CreateEvent("key-1", "Step started"),
            CreateEvent("key-2", "Tool read completed")
        };
        var snapshot = SnapshotWithJob(ForgeJobStatus.Unknown, events);

        viewModel.ApplySnapshot(snapshot);
        viewModel.ApplySnapshot(snapshot);

        Assert.AreEqual(2, viewModel.SelectedJob!.RecentEvents.Count);
        Assert.AreEqual(2, viewModel.SelectedJob.RecentEvents.Select(item => item.EventKey).Distinct().Count());
    }

    [TestMethod]
    public void ApplySnapshot_RemovesJobThatDisappears()
    {
        using var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(SnapshotWithJob(ForgeJobStatus.Unknown, Array.Empty<MonitoringEvent>()));
        Assert.AreEqual(1, viewModel.Jobs.Count);

        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = DateTimeOffset.UtcNow,
            State = MonitoringState.Idle,
            ForgeDirectoryAvailable = true,
            Jobs = Array.Empty<ForgeJobSnapshot>()
        });

        Assert.AreEqual(0, viewModel.Jobs.Count);
        Assert.IsNull(viewModel.SelectedJob);
    }

    [TestMethod]
    public void ApplySnapshot_WithCostAndWarnings_UpdatesCards()
    {
        using var viewModel = CreateViewModel();
        var snapshot = SnapshotWithJob(ForgeJobStatus.Unknown, Array.Empty<MonitoringEvent>()) with
        {
            Jobs = new[]
            {
                SnapshotWithJob(ForgeJobStatus.Unknown, Array.Empty<MonitoringEvent>()).Jobs[0] with
                {
                    ObservedCost = 0.00125
                }
            },
            Warnings = new[] { "Forge runs directory is not available." }
        };

        viewModel.ApplySnapshot(snapshot);

        Assert.AreEqual("$0.0013", viewModel.CostText);
        Assert.IsTrue(viewModel.HasWarnings);
        StringAssert.Contains(viewModel.WarningsText, "Forge runs directory");
        StringAssert.Contains(viewModel.CostDetailText, "Observed");
    }

    [TestMethod]
    public void ApplySnapshot_ErrorState_IsVisible()
    {
        using var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = DateTimeOffset.UtcNow,
            State = MonitoringState.Error,
            ForgeDirectoryAvailable = false,
            Error = "Monitoring scan failed."
        });

        Assert.AreEqual(MonitoringState.Error, viewModel.MonitoringState);
        Assert.AreEqual("Monitoring scan failed.", viewModel.StateDetail);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.WarningsText));
    }

    private MainViewModel CreateViewModel() =>
        new(
            () => throw new InvalidOperationException("Monitoring is not started in view-model tests."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5));

    private MonitoringSnapshot SnapshotWithJob(
        ForgeJobStatus status,
        IReadOnlyList<MonitoringEvent> events)
    {
        var metadata = ForgeMetadataParser.ParseFile(
            _workspace.WriteMetadata("job_vm_0001"),
            _workspace.RunsRoot);

        return new MonitoringSnapshot
        {
            ObservedAt = DateTimeOffset.UtcNow,
            State = status.IsActive() ? MonitoringState.Monitoring : MonitoringState.Idle,
            ForgeDirectoryAvailable = true,
            Jobs = new[]
            {
                new ForgeJobSnapshot
                {
                    Metadata = metadata,
                    Status = status,
                    SessionId = "ses_vm_0001",
                    LastActivity = DateTimeOffset.UtcNow,
                    ParsedEventCount = events.Count,
                    Events = events,
                    ObservedAt = DateTimeOffset.UtcNow
                }
            }
        };
    }

    private static MonitoringEvent CreateEvent(string key, string summary) =>
        new()
        {
            EventKey = key,
            JobId = "job_vm_0001",
            SessionId = "ses_vm_0001",
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(-1),
            ObservedAt = DateTimeOffset.UtcNow,
            Category = MonitoringEventCategory.Tool,
            Severity = MonitoringEventSeverity.Success,
            Summary = summary
        };
}
