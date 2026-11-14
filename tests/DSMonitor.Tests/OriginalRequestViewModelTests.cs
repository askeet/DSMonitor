using System.Diagnostics;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class OriginalRequestViewModelTests
{
    private TestWorkspace _workspace = null!;
    private SynchronizationContext? _priorContext;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SynchronizationContext.SetSynchronizationContext(_priorContext);
        _workspace.Dispose();
    }

    [TestMethod]
    public void SelectionSwitch_DiscardsLateResultAndClearsWhenSelectionIsRemoved()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(
            Snapshot(("job_a", "ses_a"), ("job_b", "ses_b")));
        var jobA = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var jobB = viewModel.Jobs.Single(job => job.JobId == "job_b");

        viewModel.SelectedJob = jobA;
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the first selection request");

        viewModel.SelectedJob = jobB;
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_b"),
            "the switched selection request");

        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("late-a"));
        PumpOnce();

        Assert.AreEqual(string.Empty, jobA.OriginalRequestText,
            "A late result for the previous selection must be discarded.");
        Assert.AreEqual(string.Empty, jobB.OriginalRequestText);

        service.Complete(
            "job_b",
            "ses_b",
            OpenCodeOriginalRequestResult.BestEffortSingleton("b-text"));
        PumpUntil(
            () => jobB.OriginalRequestText == "b-text",
            "the selected job's request");

        Assert.AreEqual("b-text", jobB.OriginalRequestText);
        Assert.AreEqual(string.Empty, jobA.OriginalRequestText);

        viewModel.SelectedJob = null;
        Assert.IsNull(viewModel.SelectedJob);
    }

    [TestMethod]
    public void SessionAssociationChange_ClearsImmediatelyInvalidatesAndRestarts()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_1")));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_1"),
            "the initial association request");
        service.Complete(
            "job_a",
            "ses_1",
            OpenCodeOriginalRequestResult.BestEffortSingleton("one"));
        PumpUntil(() => job.OriginalRequestText == "one", "the first request");

        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_2")));

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "The prior association's request must clear immediately.");
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_2"),
            "the replacement association request");

        service.Complete(
            "job_a",
            "ses_2",
            OpenCodeOriginalRequestResult.BestEffortSingleton("two"));
        PumpUntil(() => job.OriginalRequestText == "two", "the new association request");
    }

    [TestMethod]
    public void RepeatSelection_UsesThePerJobSuccessfulResult()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a"), ("job_b", "ses_b")));
        var jobA = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var jobB = viewModel.Jobs.Single(job => job.JobId == "job_b");

        viewModel.SelectedJob = jobA;
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the first request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("a-text"));
        PumpUntil(() => jobA.OriginalRequestText == "a-text", "the first result");

        viewModel.SelectedJob = jobB;
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_b"),
            "the other job request");
        viewModel.SelectedJob = jobA;
        PumpOnce();

        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"));
        Assert.AreEqual("a-text", jobA.OriginalRequestText);
    }

    [TestMethod]
    public void UnavailableResult_LeavesFieldEmptyAndIsNotTreatedAsCached()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a"), ("job_b", "ses_b")));
        var jobA = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var jobB = viewModel.Jobs.Single(job => job.JobId == "job_b");

        viewModel.SelectedJob = jobA;
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the first request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.Unavailable(
                OriginalRequestUnavailableReason.TimedOut));
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 1,
            "the unavailable result");
        PumpOnce();

        Assert.AreEqual(string.Empty, jobA.OriginalRequestText);
        Assert.AreEqual(string.Empty, jobA.OriginalRequestAttributionText);

        viewModel.SelectedJob = jobB;
        PumpOnce();
        viewModel.SelectedJob = jobA;
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 2,
            "a retry after the transient failure");
    }

    [TestMethod]
    public void SameAssociationRefresh_DoesNotClearOrRestartTheRequest()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a")));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the initial request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("stable"));
        PumpUntil(() => job.OriginalRequestText == "stable", "the first result");

        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a")));
        PumpOnce();

        Assert.AreEqual("stable", job.OriginalRequestText);
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"));
    }

    [TestMethod]
    public void ProvisionalRequest_IsClearedWhenAssistantEvidenceAppears()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a")));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        PumpUntil(() => service.Requests.Count == 1, "the provisional request");
        service.Complete("job_a", "ses_a",
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("provisional"));
        PumpUntil(() => job.OriginalRequestText == "provisional", "the provisional result");

        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        Assert.AreEqual(string.Empty, job.OriginalRequestText);
        PumpUntil(() => service.Requests.Count == 2, "the exact evidence request");
        service.Complete("job_a", "ses_a", null, new[] { "msg_a" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("exact"));
        PumpUntil(() => job.OriginalRequestText == "exact", "the exact result");
    }

    [TestMethod]
    public void ProvisionalRequest_StatusChangeRetriesWithoutRepeatingUnchangedRefreshes()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        var snapshot = Snapshot(("job_a", "ses_a"));
        viewModel.ApplySnapshot(snapshot);
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        PumpUntil(() => service.Requests.Count == 1, "the initial request");
        service.Complete("job_a", "ses_a", OpenCodeOriginalRequestResult.None());
        PumpOnce();
        viewModel.ApplySnapshot(snapshot);
        Assert.AreEqual(1, service.Requests.Count);

        var updatedJob = snapshot.Jobs.Single() with { Status = ForgeJobStatus.Done };
        viewModel.ApplySnapshot(snapshot with { Jobs = new[] { updatedJob } });
        PumpUntil(() => service.Requests.Count == 2, "the status-triggered retry");
    }

    [TestMethod]
    public void ProvisionalRequest_LateResultCannotOverwriteAssistantEvidence()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a")));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        PumpUntil(() => service.Requests.Count == 1, "the provisional request");

        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        PumpUntil(() => service.Requests.Count == 2, "the exact evidence request");
        service.Complete("job_a", "ses_a", null, Array.Empty<string>(),
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("stale"));
        PumpOnce();
        Assert.AreEqual(string.Empty, job.OriginalRequestText);
        service.Complete("job_a", "ses_a", null, new[] { "msg_a" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("exact"));
        PumpUntil(() => job.OriginalRequestText == "exact", "the exact result");
    }

    [TestMethod]
    public void ProvisionalRequest_ReselectingJobStartsFreshLookup()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", "ses_a"), ("job_b", "ses_b")));
        var first = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var second = viewModel.Jobs.Single(job => job.JobId == "job_b");
        viewModel.SelectedJob = first;
        PumpUntil(() => service.Requests.Count == 1, "the provisional request");
        service.Complete("job_a", "ses_a",
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("old"));
        PumpUntil(() => first.OriginalRequestText == "old", "the provisional result");
        viewModel.SelectedJob = second;
        PumpUntil(() => service.Requests.Count == 2, "the other job request");
        viewModel.SelectedJob = first;
        Assert.AreEqual(string.Empty, first.OriginalRequestText);
        PumpUntil(() => service.Requests.Count == 3, "the refreshed provisional request");
    }

    [TestMethod]
    public void MonitoringException_ClearsRequestsAndBlocksStaleSelectionUntilHealthy()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        var snapshot = Snapshot(("job_a", "ses_a"), ("job_b", "ses_b"));
        viewModel.ApplySnapshot(snapshot);
        var first = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var second = viewModel.Jobs.Single(job => job.JobId == "job_b");
        viewModel.SelectedJob = first;
        PumpUntil(() => service.Requests.Count == 1, "the healthy request");
        service.Complete("job_a", "ses_a",
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("old"));
        PumpUntil(() => first.OriginalRequestText == "old", "the healthy result");

        typeof(MainViewModel).GetMethod("HandleServiceFailure",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
        Assert.AreEqual(string.Empty, first.OriginalRequestText);
        viewModel.SelectedJob = second;
        PumpOnce();
        Assert.AreEqual(1, service.Requests.Count);
        Assert.AreEqual(string.Empty, second.OriginalRequestText);

        viewModel.ApplySnapshot(snapshot);
        PumpUntil(() => service.Requests.Count == 2, "the recovered request");
    }

    [TestMethod]
    public void EvidenceChange_ClearsImmediatelyInvalidatesAndRestarts()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" &&
                request.ObservedAssistantMessageIds.SequenceEqual(new[] { "msg_a" })),
            "the initial evidence request");
        service.Complete(
            "job_a",
            "ses_a",
            null,
            new[] { "msg_a" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("first"));
        PumpUntil(() => job.OriginalRequestText == "first", "the first evidence result");

        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_b"));

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "Prior evidence's request must clear immediately.");
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" &&
                request.ObservedAssistantMessageIds.SequenceEqual(new[] { "msg_b" })),
            "the replacement evidence request");

        service.Complete(
            "job_a",
            "ses_a",
            null,
            new[] { "msg_b" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("second"));
        PumpUntil(() => job.OriginalRequestText == "second", "the new evidence result");
    }

    [TestMethod]
    public void ChangedEvidence_DiscardsLateResultForPriorEvidence()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" &&
                request.ObservedAssistantMessageIds.SequenceEqual(new[] { "msg_a" })),
            "the first evidence request");

        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_b"));
        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" &&
                request.ObservedAssistantMessageIds.SequenceEqual(new[] { "msg_b" })),
            "the second evidence request");

        service.Complete(
            "job_a",
            "ses_a",
            null,
            new[] { "msg_a" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("stale"));
        PumpOnce();

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "A late result for prior evidence must be discarded.");

        service.Complete(
            "job_a",
            "ses_a",
            null,
            new[] { "msg_b" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the current evidence result");
    }

    [TestMethod]
    public void SameEvidenceRefresh_DoesNotClearOrRestartTheRequest()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" &&
                request.ObservedAssistantMessageIds.SequenceEqual(new[] { "msg_a" })),
            "the initial evidence request");
        service.Complete(
            "job_a",
            "ses_a",
            null,
            new[] { "msg_a" },
            OpenCodeOriginalRequestResult.ObservedJobCorrelation("stable"));
        PumpUntil(() => job.OriginalRequestText == "stable", "the first evidence result");

        viewModel.ApplySnapshot(SnapshotWithEvidence("job_a", "ses_a", "msg_a"));
        PumpOnce();

        Assert.AreEqual("stable", job.OriginalRequestText);
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"));
    }


    [TestMethod]
    public void ContradictoryEvidence_AppliesAmbiguousWithoutRequest()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithMixedEvidenceSources("job_a", "ses_a"));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;
        PumpOnce();

        Assert.AreEqual(string.Empty, job.OriginalRequestText);
        StringAssert.Contains(job.OriginalRequestAttributionText, "Ambiguous");
        Assert.AreEqual(0, service.Requests.Count);
    }

    [TestMethod]
    public void ConflictAfterSingleton_ClearsImmediatelyAndDoesNotRetry()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the healthy no-evidence request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("old"));
        PumpUntil(() => job.OriginalRequestText == "old", "the singleton result");

        viewModel.ApplySnapshot(SnapshotWithMixedEvidenceSources("job_a", "ses_a"));

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "Conflicting evidence must clear the prior singleton immediately.");
        StringAssert.Contains(job.OriginalRequestAttributionText, "Ambiguous");
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"),
            "Conflicting evidence must not be retried as a service query.");
    }

    [TestMethod]
    public void ConflictWhileSingletonInFlight_DiscardsCompletionAndRecovers()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the healthy no-evidence request");

        viewModel.ApplySnapshot(SnapshotWithMixedEvidenceSources("job_a", "ses_a"));
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("stale"));
        PumpOnce();

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "A completion for prior healthy identity must not publish after conflict.");
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"));

        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 2,
            "the healthy recovery request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the recovered request");
    }

    [TestMethod]
    public void UnavailableProjection_ClearsAndDoesNotRequestUntilHealthy()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the healthy no-evidence request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("old"));
        PumpUntil(() => job.OriginalRequestText == "old", "the singleton result");

        viewModel.ApplySnapshot(
            SnapshotWithHealth("job_a", "ses_a", false) with
            {
                State = MonitoringState.Error
            });

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "An unavailable projection must clear the prior result immediately.");
        Assert.AreEqual(string.Empty, job.OriginalRequestAttributionText);
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        PumpOnce();
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"),
            "Unavailable monitoring must not retry from stale session metadata.");

        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 2,
            "the healthy observation request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the healthy recovery result");
    }

    [TestMethod]
    public void UnavailableProjectionWhileInFlight_DiscardsCompletionAndRecovers()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the healthy no-evidence request");

        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", false));
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("stale"));
        PumpOnce();
        Assert.AreEqual(string.Empty, job.OriginalRequestText);

        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 2,
            "the healthy recovery request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the recovered request");
    }

    [TestMethod]
    public void MonitoringError_ClearsEvenWhenJobProjectionLooksHealthy()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_a"),
            "the healthy request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("old"));
        PumpUntil(() => job.OriginalRequestText == "old", "the singleton result");

        viewModel.ApplySnapshot(
            SnapshotWithHealth("job_a", "ses_a", true) with
            {
                State = MonitoringState.Error
            });

        Assert.AreEqual(string.Empty, job.OriginalRequestText);
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        PumpOnce();
        Assert.AreEqual(
            1,
            service.Requests.Count(request => request.SessionId == "ses_a"));

        viewModel.ApplySnapshot(SnapshotWithHealth("job_a", "ses_a", true));
        PumpUntil(
            () => service.Requests.Count(request => request.SessionId == "ses_a") == 2,
            "the recovered monitoring request");
        service.Complete(
            "job_a",
            "ses_a",
            OpenCodeOriginalRequestResult.BestEffortSingleton("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the recovered result");
    }

    [TestMethod]
    public void StartTimeChange_ClearsImmediatelyInvalidatesAndRestarts()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        var firstStart = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var secondStart = DateTimeOffset.FromUnixTimeMilliseconds(2_000);
        viewModel.ApplySnapshot(SnapshotAt(("job_a", "ses_a", firstStart)));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" && request.StartedAt == firstStart),
            "the initial start-time request");
        service.Complete(
            "job_a",
            "ses_a",
            firstStart,
            OpenCodeOriginalRequestResult.BestEffortSingleton("one"));
        PumpUntil(() => job.OriginalRequestText == "one", "the first request");

        viewModel.ApplySnapshot(SnapshotAt(("job_a", "ses_a", secondStart)));

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "The prior start time's request must clear immediately.");
        CollectionAssert.Contains(service.InvalidatedJobs, "job_a");
        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" && request.StartedAt == secondStart),
            "the replacement start-time request");

        service.Complete(
            "job_a",
            "ses_a",
            secondStart,
            OpenCodeOriginalRequestResult.BestEffortSingleton("two"));
        PumpUntil(() => job.OriginalRequestText == "two", "the new start-time request");
    }

    [TestMethod]
    public void ChangedStart_DiscardsLateResultForPriorStart()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        var firstStart = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var secondStart = DateTimeOffset.FromUnixTimeMilliseconds(2_000);
        viewModel.ApplySnapshot(SnapshotAt(("job_a", "ses_a", firstStart)));
        var job = viewModel.Jobs.Single();
        viewModel.SelectedJob = job;

        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" && request.StartedAt == firstStart),
            "the first start-time request");

        viewModel.ApplySnapshot(SnapshotAt(("job_a", "ses_a", secondStart)));
        PumpUntil(
            () => service.Requests.Any(request =>
                request.SessionId == "ses_a" && request.StartedAt == secondStart),
            "the second start-time request");

        service.Complete(
            "job_a",
            "ses_a",
            firstStart,
            OpenCodeOriginalRequestResult.BestEffortSingleton("stale"));
        PumpOnce();

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "A late result for the prior start time must be discarded.");

        service.Complete(
            "job_a",
            "ses_a",
            secondStart,
            OpenCodeOriginalRequestResult.BestEffortSingleton("fresh"));
        PumpUntil(() => job.OriginalRequestText == "fresh", "the current start result");
    }

    [TestMethod]
    public void MissingOrInvalidSession_LeavesFieldEmptyWithoutARequest()
    {
        var service = new ControlledOriginalRequestService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(("job_a", null), ("job_b", "not-a-session")));

        viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == "job_a");
        PumpOnce();
        Assert.AreEqual(string.Empty, viewModel.SelectedJob!.OriginalRequestText);
        Assert.AreEqual(0, service.Requests.Count);

        viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == "job_b");
        PumpOnce();
        Assert.AreEqual(string.Empty, viewModel.SelectedJob!.OriginalRequestText);
        Assert.AreEqual(0, service.Requests.Count);
    }

    private MainViewModel CreateViewModel(IOriginalRequestService service) =>
        new(
            () => throw new InvalidOperationException("Monitoring is not started."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5),
            service);

    private static MonitoringSnapshot Snapshot(
        params (string JobId, string? SessionId)[] jobs) =>
        SnapshotAt(
            jobs
                .Select(job => (job.JobId, job.SessionId, (DateTimeOffset?)null))
                .ToArray());

    private static MonitoringSnapshot SnapshotAt(
        params (string JobId, string? SessionId, DateTimeOffset? StartedAt)[] jobs) => new()
    {
        ObservedAt = DateTimeOffset.UtcNow,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = jobs.Select(job => new ForgeJobSnapshot
        {
            Metadata = new ForgeJobMetadata
            {
                JobId = job.JobId,
                MetadataPath = Path.Combine(@"C:\synthetic", job.JobId + ".json"),
                LogPath = Path.Combine(@"C:\synthetic", job.JobId + ".log"),
                Started = job.StartedAt
            },
            Status = ForgeJobStatus.Unknown,
            SessionId = job.SessionId,
            ObservedAt = DateTimeOffset.UtcNow,
            LiveActivity = new LiveActivitySnapshot { IsHealthy = true }
        }).ToArray()
    };

    private static MonitoringSnapshot SnapshotWithEvidence(
        string jobId,
        string sessionId,
        params string[] messageIds) => new()
    {
        ObservedAt = DateTimeOffset.UtcNow,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = new[]
        {
            new ForgeJobSnapshot
            {
                Metadata = new ForgeJobMetadata
                {
                    JobId = jobId,
                    MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                    LogPath = Path.Combine(@"C:\synthetic", jobId + ".log")
                },
                Status = ForgeJobStatus.Unknown,
                SessionId = sessionId,
                ObservedAt = DateTimeOffset.UtcNow,
                LiveActivity = new LiveActivitySnapshot
                {
                    IsHealthy = true,
                    History = messageIds.Select(messageId => new LiveActivityHistoryEntry(
                        jobId,
                        sessionId,
                        messageId,
                        "part_" + messageId,
                        "Assistant-reported text",
                        "text",
                        Path.Combine(@"C:\synthetic", jobId + ".log"),
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        "digest",
                        null)).ToArray()
                }
            }
        }
    };

    private static MonitoringSnapshot SnapshotWithHealth(
        string jobId,
        string sessionId,
        bool isHealthy) => new()
    {
        ObservedAt = DateTimeOffset.UtcNow,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = new[]
        {
            new ForgeJobSnapshot
            {
                Metadata = new ForgeJobMetadata
                {
                    JobId = jobId,
                    MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                    LogPath = Path.Combine(@"C:\synthetic", jobId + ".log")
                },
                Status = ForgeJobStatus.Unknown,
                SessionId = sessionId,
                ObservedAt = DateTimeOffset.UtcNow,
                LiveActivity = new LiveActivitySnapshot
                {
                    IsHealthy = isHealthy,
                    Availability = isHealthy
                        ? "Bounded source re-read; synthetic test projection."
                        : "N/A - monitoring scan failed; no cached activity is shown."
                }
            }
        }
    };

    private static MonitoringSnapshot SnapshotWithMixedEvidenceSources(
        string jobId,
        string sessionId) => new()
    {
        ObservedAt = DateTimeOffset.UtcNow,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = new[]
        {
            new ForgeJobSnapshot
            {
                Metadata = new ForgeJobMetadata
                {
                    JobId = jobId,
                    MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                    LogPath = Path.Combine(@"C:\synthetic", jobId + ".log")
                },
                Status = ForgeJobStatus.Unknown,
                SessionId = sessionId,
                ObservedAt = DateTimeOffset.UtcNow,
                LiveActivity = new LiveActivitySnapshot
                {
                    IsHealthy = true,
                    History = new[]
                    {
                        new LiveActivityHistoryEntry(
                            jobId,
                            sessionId,
                            "msg_a",
                            "part_a",
                            "Assistant-reported text",
                            "text",
                            @"C:\synthetic\a.log",
                            0,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            "digest",
                            null),
                        new LiveActivityHistoryEntry(
                            jobId,
                            sessionId,
                            "msg_b",
                            "part_b",
                            "Assistant-reported text",
                            "text",
                            @"C:\synthetic\b.log",
                            1,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            "digest",
                            null)
                    }
                }
            }
        }
    };

    private static void PumpOnce() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void PumpUntil(Func<bool> condition, string description)
    {
        if (condition())
        {
            return;
        }

        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        void Tick(object? sender, EventArgs args)
        {
            if (condition() || stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            {
                frame.Continue = false;
            }
        }

        timer.Tick += Tick;
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Tick -= Tick;
            timer.Stop();
        }

        Assert.IsTrue(condition(), $"Timed out waiting for {description}.");
    }

    private sealed class ControlledOriginalRequestService : IOriginalRequestService
    {
        private readonly object _gate = new();
        private readonly List<PendingRequest> _pending = new();

        public List<Request> Requests { get; } = new();
        public List<string> InvalidatedJobs { get; } = new();

        public Task<OpenCodeOriginalRequestResult> GetOriginalRequestAsync(
            string jobId,
            string sessionId,
            string? verifiedUserMessageId,
            CancellationToken cancellationToken,
            DateTimeOffset? jobStartedAt = null,
            IReadOnlyList<string>? observedAssistantMessageIds = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<OpenCodeOriginalRequestResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                Requests.Add(new Request(
                    jobId,
                    sessionId,
                    jobStartedAt,
                    observedAssistantMessageIds?.ToArray() ??
                        Array.Empty<string>()));
                _pending.Add(new PendingRequest(
                    jobId,
                    sessionId,
                    jobStartedAt,
                    observedAssistantMessageIds?.ToArray() ??
                        Array.Empty<string>(),
                    completion));
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(
            string jobId,
            string sessionId,
            OpenCodeOriginalRequestResult result) =>
            Complete(jobId, sessionId, startedAt: null, result);

        public void Complete(
            string jobId,
            string sessionId,
            DateTimeOffset? startedAt,
            OpenCodeOriginalRequestResult result) =>
            Complete(jobId, sessionId, startedAt, null, result);

        public void Complete(
            string jobId,
            string sessionId,
            DateTimeOffset? startedAt,
            IReadOnlyList<string>? observedAssistantMessageIds,
            OpenCodeOriginalRequestResult result)
        {
            PendingRequest pending;
            lock (_gate)
            {
                pending = _pending.Last(item =>
                    string.Equals(item.JobId, jobId, StringComparison.Ordinal) &&
                    string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                    item.StartedAt == startedAt &&
                    (observedAssistantMessageIds is null ||
                     item.ObservedAssistantMessageIds
                         .OrderBy(id => id, StringComparer.Ordinal)
                         .SequenceEqual(
                             observedAssistantMessageIds
                                 .OrderBy(id => id, StringComparer.Ordinal),
                             StringComparer.Ordinal)));
                _pending.Remove(pending);
            }

            pending.Completion.SetResult(result);
        }

        public void InvalidateJob(string jobId)
        {
            lock (_gate)
            {
                InvalidatedJobs.Add(jobId);
            }
        }

        public void ClearCache()
        {
        }

        public void Dispose()
        {
        }

        public sealed record Request(
            string JobId,
            string SessionId,
            DateTimeOffset? StartedAt,
            IReadOnlyList<string> ObservedAssistantMessageIds);

        private sealed record PendingRequest(
            string JobId,
            string SessionId,
            DateTimeOffset? StartedAt,
            IReadOnlyList<string> ObservedAssistantMessageIds,
            TaskCompletionSource<OpenCodeOriginalRequestResult> Completion);
    }
}
