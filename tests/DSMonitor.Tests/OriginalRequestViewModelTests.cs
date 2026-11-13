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
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("one"));
        PumpUntil(() => job.OriginalRequestText == "one", "the first request");
        StringAssert.Contains(
            job.OriginalRequestAttributionText,
            "not verified job ownership");

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
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("two"));
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
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("stale"));
        PumpOnce();

        Assert.AreEqual(string.Empty, job.OriginalRequestText,
            "A late result for the prior start time must be discarded.");

        service.Complete(
            "job_a",
            "ses_a",
            secondStart,
            OpenCodeOriginalRequestResult.BestEffortTimeCorrelation("fresh"));
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
            ObservedAt = DateTimeOffset.UtcNow
        }).ToArray()
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
            DateTimeOffset? jobStartedAt = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<OpenCodeOriginalRequestResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                Requests.Add(new Request(jobId, sessionId, jobStartedAt));
                _pending.Add(new PendingRequest(
                    jobId,
                    sessionId,
                    jobStartedAt,
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
            OpenCodeOriginalRequestResult result)
        {
            PendingRequest pending;
            lock (_gate)
            {
                pending = _pending.Last(item =>
                    string.Equals(item.JobId, jobId, StringComparison.Ordinal) &&
                    string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                    item.StartedAt == startedAt);
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
            DateTimeOffset? StartedAt);

        private sealed record PendingRequest(
            string JobId,
            string SessionId,
            DateTimeOffset? StartedAt,
            TaskCompletionSource<OpenCodeOriginalRequestResult> Completion);
    }
}
