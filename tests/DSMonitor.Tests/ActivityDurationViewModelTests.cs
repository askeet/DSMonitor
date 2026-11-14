using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class ActivityDurationViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Started = Now.AddMinutes(-5);
    private const string SourcePath = @"C:\synthetic\runs\job.log";

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
    public void SelectedJob_LoadsOnce_AndKeepsDurationAcrossRowRebuild()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        var snapshot = Snapshot(Job("job_a", "ses_a", Text("msg_a")));

        viewModel.ApplySnapshot(snapshot);
        var job = viewModel.SelectedJob!;
        PumpUntil(
            () => service.Requests.Count == 1,
            "the initial duration request");
        var sourceStarted = Text("msg_a").SourceTimestamp!.Value;
        Assert.AreEqual(FormatStarted(sourceStarted), job.LiveActivityHistory.Single().StartedText);

        service.Complete(
            "job_a",
            "ses_a",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromMilliseconds(14_781))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "14s",
            "the applied duration");
        var messageCreated = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        Assert.AreEqual(FormatStarted(messageCreated), job.LiveActivityHistory.Single().StartedText);

        viewModel.ApplySnapshot(snapshot);
        PumpOnce();

        Assert.AreEqual(1, service.Requests.Count);
        Assert.AreEqual("14s", job.LiveActivityHistory.Single().DurationText);
        Assert.AreEqual(FormatStarted(messageCreated), job.LiveActivityHistory.Single().StartedText);
        StringAssert.Contains(
            job.LiveActivityHistory.Single().DurationTooltip,
            "OpenCode assistant message duration");
    }

    [TestMethod]
    public void SharedAssistantAcrossThreeJobs_IsSuppressedWithoutAssumingTwo()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(
            Job("job_a", "ses_shared", Text("msg_shared")),
            Job("job_b", "ses_shared", Text("msg_shared")),
            Job("job_c", "ses_shared", Text("msg_shared"))));

        var job = viewModel.SelectedJob!;
        PumpOnce();

        Assert.AreEqual(0, service.Requests.Count);
        Assert.AreEqual("N/A", job.LiveActivityHistory.Single().DurationText);
    }

    [TestMethod]
    public void RemovedSharedClaim_ReleasesDurationAndReloads()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(
            Job("job_a", "ses_shared", Text("msg_shared")),
            Job("job_b", "ses_shared", Text("msg_shared"))));
        var job = viewModel.SelectedJob!;
        PumpOnce();
        Assert.AreEqual(0, service.Requests.Count);

        viewModel.ApplySnapshot(Snapshot(
            Job("job_a", "ses_shared", Text("msg_shared"))));
        PumpUntil(() => service.Requests.Count == 1, "the released claim request");
        service.Complete(
            "job_a",
            "ses_shared",
            new[] { "msg_shared" },
            Available(("msg_shared", TimeSpan.FromSeconds(1))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "1s",
            "the released claim duration");
    }

    [TestMethod]
    public void LifecycleChange_RetriesIncompleteDuration()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        var snapshot = Snapshot(Job("job_life", "ses_life", Text("msg_a")));
        viewModel.ApplySnapshot(snapshot);
        var job = viewModel.SelectedJob!;

        PumpUntil(() => service.Requests.Count == 1, "the incomplete request");
        service.Complete(
            "job_life",
            "ses_life",
            new[] { "msg_a" },
            OpenCodeActivityDurationsResult.Available(
                new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal),
                isComplete: false));
        PumpOnce();
        Assert.AreEqual("N/A", job.LiveActivityHistory.Single().DurationText);

        var lifecycleSnapshot = snapshot with
        {
            Jobs = new[]
            {
                snapshot.Jobs[0] with
                {
                    LiveActivity = snapshot.Jobs[0].LiveActivity with
                    {
                        LatestLifecycleByteOffset = 10
                    }
                }
            }
        };
        viewModel.ApplySnapshot(lifecycleSnapshot);
        PumpUntil(() => service.Requests.Count == 2, "the lifecycle retry");
        service.Complete(
            "job_life",
            "ses_life",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(1))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "1s",
            "the lifecycle retry value");
    }

    [TestMethod]
    public void ToolDurationWins_OverMessageDuration()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(
            Job(
                "job_tool",
                "ses_tool",
                Tool("msg_tool", TimeSpan.FromSeconds(2)))));

        var job = viewModel.SelectedJob!;
        PumpUntil(() => service.Requests.Count == 1, "the tool duration request");
        service.Complete(
            "job_tool",
            "ses_tool",
            new[] { "msg_tool" },
            Available(("msg_tool", TimeSpan.FromMilliseconds(14_781))));
        PumpOnce();

        var row = job.LiveActivityHistory.Single();
        Assert.AreEqual("2s", row.DurationText);
        StringAssert.Contains(row.DurationTooltip, "same-part");
    }

    [TestMethod]
    public void SelectionSwitch_DiscardsLateResult()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(
            Job("job_a", "ses_a", Text("msg_a")),
            Job("job_b", "ses_b", Text("msg_b"))));
        var jobA = viewModel.Jobs.Single(job => job.JobId == "job_a");
        var jobB = viewModel.Jobs.Single(job => job.JobId == "job_b");
        var sourceA = Text("msg_a").SourceTimestamp!.Value;
        var sourceB = Text("msg_b").SourceTimestamp!.Value;
        Assert.AreEqual(FormatStarted(sourceA), jobA.LiveActivityHistory.Single().StartedText);
        Assert.AreEqual(FormatStarted(sourceB), jobB.LiveActivityHistory.Single().StartedText);

        viewModel.SelectedJob = jobA;
        PumpUntil(
            () => service.Requests.Any(request => request.JobId == "job_a"),
            "the first job request");
        viewModel.SelectedJob = jobB;
        PumpUntil(
            () => service.Requests.Any(request => request.JobId == "job_b"),
            "the second job request");

        service.Complete(
            "job_a",
            "ses_a",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(1))));
        PumpOnce();
        Assert.AreEqual("N/A", jobA.LiveActivityHistory.Single().DurationText);
        Assert.AreEqual(FormatStarted(sourceA), jobA.LiveActivityHistory.Single().StartedText);

        service.Complete(
            "job_b",
            "ses_b",
            new[] { "msg_b" },
            Available(("msg_b", TimeSpan.FromSeconds(2))));
        PumpUntil(
            () => jobB.LiveActivityHistory.Single().DurationText == "2s",
            "the selected job duration");
        Assert.AreEqual("N/A", jobA.LiveActivityHistory.Single().DurationText);
        Assert.AreEqual(FormatStarted(sourceA), jobA.LiveActivityHistory.Single().StartedText);
        Assert.AreEqual(
            FormatStarted(DateTimeOffset.FromUnixTimeMilliseconds(1_000)),
            jobB.LiveActivityHistory.Single().StartedText);
    }

    [TestMethod]
    public void EvidenceChange_ClearsAndReloads()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(Job("job_ev", "ses_ev", Text("msg_a"))));
        var job = viewModel.SelectedJob!;

        PumpUntil(() => service.Requests.Count == 1, "the initial evidence request");
        service.Complete(
            "job_ev",
            "ses_ev",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(1))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "1s",
            "the first evidence value");

        viewModel.ApplySnapshot(Snapshot(
            Job("job_ev", "ses_ev", Text("msg_a"), Text("msg_b"))));
        Assert.AreEqual("N/A", job.LiveActivityHistory[0].DurationText);
        Assert.AreEqual("N/A", job.LiveActivityHistory[1].DurationText);

        PumpUntil(() => service.Requests.Count == 2, "the replacement evidence request");
        Assert.AreEqual(
            2,
            service.Requests[^1].MessageIds.Count);
        service.Complete(
            "job_ev",
            "ses_ev",
            new[] { "msg_a", "msg_b" },
            Available(
                ("msg_a", TimeSpan.FromSeconds(1)),
                ("msg_b", TimeSpan.FromSeconds(2))));
        PumpUntil(
            () => job.LiveActivityHistory.All(row => row.DurationText != "N/A"),
            "the replacement evidence values");
    }

    [TestMethod]
    public void MonitoringError_ClearsAndInvalidates()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(Job("job_error", "ses_error", Text("msg_a"))));
        var job = viewModel.SelectedJob!;

        PumpUntil(() => service.Requests.Count == 1, "the healthy request");
        service.Complete(
            "job_error",
            "ses_error",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(1))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "1s",
            "the healthy value");

        viewModel.ApplySnapshot(new MonitoringSnapshot
        {
            ObservedAt = Now,
            State = MonitoringState.Error,
            ForgeDirectoryAvailable = false,
            Error = "synthetic scan failure",
            Jobs = new[]
            {
                Job("job_error", "ses_error", Text("msg_a"))
            }
        });

        Assert.AreEqual("N/A", job.LiveActivityHistory.Single().DurationText);
        CollectionAssert.Contains(service.InvalidatedJobs, "job_error");
        Assert.AreEqual(1, service.Requests.Count);
    }

    [TestMethod]
    public void SessionChange_DiscardsLateResultAndLoadsReplacement()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        viewModel.ApplySnapshot(Snapshot(Job("job_session", "ses_old", Text("msg_a"))));
        var job = viewModel.SelectedJob!;

        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_old"),
            "the old session request");
        var sourceStarted = Text("msg_a").SourceTimestamp!.Value;
        Assert.AreEqual(FormatStarted(sourceStarted), job.LiveActivityHistory.Single().StartedText);
        viewModel.ApplySnapshot(Snapshot(Job("job_session", "ses_new", Text("msg_a"))));
        PumpUntil(
            () => service.Requests.Any(request => request.SessionId == "ses_new"),
            "the new session request");

        service.Complete(
            "job_session",
            "ses_old",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(1))));
        PumpOnce();
        Assert.AreEqual("N/A", job.LiveActivityHistory.Single().DurationText);
        Assert.AreEqual(FormatStarted(sourceStarted), job.LiveActivityHistory.Single().StartedText);

        service.Complete(
            "job_session",
            "ses_new",
            new[] { "msg_a" },
            Available(("msg_a", TimeSpan.FromSeconds(2))));
        PumpUntil(
            () => job.LiveActivityHistory.Single().DurationText == "2s",
            "the replacement session value");
        Assert.AreEqual(
            FormatStarted(DateTimeOffset.FromUnixTimeMilliseconds(1_000)),
            job.LiveActivityHistory.Single().StartedText);
    }

    [TestMethod]
    public void RepeatedSnapshots_DoNotPollOrReload()
    {
        var service = new ControlledDurationService();
        using var viewModel = CreateViewModel(service);
        var snapshot = Snapshot(Job("job_poll", "ses_poll", Text("msg_a")));

        viewModel.ApplySnapshot(snapshot);
        PumpUntil(() => service.Requests.Count == 1, "the initial request");
        viewModel.ApplySnapshot(snapshot);
        viewModel.ApplySnapshot(snapshot);
        PumpOnce();

        Assert.AreEqual(1, service.Requests.Count);
    }

    private MainViewModel CreateViewModel(IOpenCodeActivityDurationService service) =>
        new(
            () => throw new InvalidOperationException("Monitoring is not started."),
            Dispatcher.CurrentDispatcher,
            _workspace.RunsRoot,
            TimeSpan.FromSeconds(5),
            DisabledOriginalRequestService.Instance,
            service);

    private static MonitoringSnapshot Snapshot(params ForgeJobSnapshot[] jobs) => new()
    {
        ObservedAt = Now,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = jobs
    };

    private static ForgeJobSnapshot Job(
        string jobId,
        string sessionId,
        params LiveActivityHistoryEntry[] entries) => new()
    {
        Metadata = new ForgeJobMetadata
        {
            JobId = jobId,
            MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
            LogPath = Path.Combine(@"C:\synthetic", jobId + ".log"),
            Started = Started,
            ExplicitStatus = ForgeJobStatus.Unknown
        },
        Status = ForgeJobStatus.Unknown,
        SessionId = sessionId,
        ObservedAt = Now,
        LiveActivity = new LiveActivitySnapshot
        {
            IsHealthy = true,
            History = entries
                .Select(entry => entry with
                {
                    JobId = jobId,
                    SessionId = sessionId
                })
                .ToArray(),
            Availability = "Bounded synthetic activity."
        }
    };

    private static LiveActivityHistoryEntry Text(string messageId) =>
        new(
            "job",
            "ses",
            messageId,
            "part_" + messageId,
            "Assistant-reported text",
            "synthetic text",
            SourcePath,
            0,
            Now.AddMinutes(-1),
            Now,
            "digest_" + messageId,
            null);

    private static LiveActivityHistoryEntry Tool(string messageId, TimeSpan duration)
    {
        var start = Now.AddMinutes(-1).ToUnixTimeMilliseconds();
        return new LiveActivityHistoryEntry(
            "job",
            "ses",
            messageId,
            "part_" + messageId,
            "Observed tool activity",
            "synthetic tool",
            SourcePath,
            0,
            Now.AddMinutes(-1),
            Now,
            "digest_" + messageId,
            duration)
        {
            DurationStartUnixMilliseconds = start,
            DurationEndUnixMilliseconds = start + (long)duration.TotalMilliseconds
        };
    }

    private static OpenCodeActivityDurationsResult Available(
        params (string MessageId, TimeSpan Duration)[] durations)
    {
        var map = new Dictionary<string, OpenCodeMessageDuration>(StringComparer.Ordinal);
        foreach (var item in durations)
        {
            map[item.MessageId] = new OpenCodeMessageDuration(
                item.MessageId,
                DateTimeOffset.FromUnixTimeMilliseconds(1_000),
                DateTimeOffset.FromUnixTimeMilliseconds(1_000) + item.Duration,
                item.Duration);
        }

        return OpenCodeActivityDurationsResult.Available(map, isComplete: true);
    }

    private static string FormatStarted(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);

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

    private sealed class ControlledDurationService : IOpenCodeActivityDurationService
    {
        private readonly object _gate = new();
        private readonly List<Pending> _pending = new();

        public List<DurationRequest> Requests { get; } = new();
        public List<string> InvalidatedJobs { get; } = new();

        public Task<OpenCodeActivityDurationsResult> GetDurationsAsync(
            string jobId,
            string sessionId,
            IReadOnlyList<string> observedAssistantMessageIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<OpenCodeActivityDurationsResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ids = observedAssistantMessageIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            lock (_gate)
            {
                Requests.Add(new DurationRequest(jobId, sessionId, ids));
                _pending.Add(new Pending(jobId, sessionId, ids, completion));
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(
            string jobId,
            string sessionId,
            IReadOnlyList<string> messageIds,
            OpenCodeActivityDurationsResult result)
        {
            var ids = messageIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Pending pending;
            lock (_gate)
            {
                pending = _pending.Last(item =>
                    string.Equals(item.JobId, jobId, StringComparison.Ordinal) &&
                    string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                    item.MessageIds.SequenceEqual(ids, StringComparer.Ordinal));
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

        public sealed record DurationRequest(
            string JobId,
            string SessionId,
            IReadOnlyList<string> MessageIds);

        private sealed record Pending(
            string JobId,
            string SessionId,
            IReadOnlyList<string> MessageIds,
            TaskCompletionSource<OpenCodeActivityDurationsResult> Completion);
    }
}
