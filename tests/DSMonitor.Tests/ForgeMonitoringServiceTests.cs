using System.Text.Json;
using System.Threading.Channels;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ForgeMonitoringServiceTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public async Task Run_InitialScan_DiscoversExistingJobAndCreatesLogs()
    {
        var jobId = "job_existing_0001";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(
            jobId,
            TestWorkspace.StepStart(1_800_000_000_000),
            TestWorkspace.ToolUse(1_800_000_000_500, "read", "completed", 1_800_000_000_500, 1_800_000_001_000),
            TestWorkspace.StepFinish(1_800_000_002_000, 0.00125));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.NextAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MonitoringState.Idle, snapshot.State);
        Assert.IsTrue(snapshot.ForgeDirectoryAvailable);
        Assert.AreEqual(1, snapshot.TotalCount);
        Assert.AreEqual(1, snapshot.UnknownCount);
        Assert.AreEqual(0, snapshot.ActiveCount);
        Assert.AreEqual("ses_test0001", snapshot.Jobs[0].SessionId);
        Assert.AreEqual(ForgeJobStatus.Unknown, snapshot.Jobs[0].Status);
        Assert.AreEqual(3, snapshot.Jobs[0].ParsedEventCount);
        Assert.IsNotNull(snapshot.ObservedCost);
        Assert.AreEqual(0.00125, snapshot.ObservedCost.Value, 1e-12);
        Assert.IsTrue(snapshot.Jobs[0].Events.Count >= 3);

        Assert.AreEqual(1, Directory.GetFiles(_workspace.LogsRoot, "*.log", SearchOption.AllDirectories).Length);
        Assert.AreEqual(1, Directory.GetFiles(_workspace.LogsRoot, "*.jsonl", SearchOption.AllDirectories).Length);

        await harness.StopAsync();
        var logPath = Directory.GetFiles(_workspace.LogsRoot, "*.log", SearchOption.AllDirectories).Single();
        var text = File.ReadAllText(logPath);
        StringAssert.Contains(text, "Monitoring started");
        StringAssert.Contains(text, "Monitoring stopped");
        Assert.IsTrue(CanOpenExclusively(logPath));
    }

    [TestMethod]
    public async Task Run_NoJobs_IsIdleWithZeroUnknown()
    {
        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.NextAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MonitoringState.Idle, snapshot.State);
        Assert.AreEqual(0, snapshot.TotalCount);
        Assert.AreEqual(0, snapshot.UnknownCount);
        Assert.IsTrue(snapshot.ForgeDirectoryAvailable);
    }

    [TestMethod]
    public async Task Run_DoesNotOverlapScansWhenSnapshotHandlingExceedsRefreshInterval()
    {
        using var cancellation = new CancellationTokenSource();
        var callbacks = 0;
        var activeCallbacks = 0;
        var maxConcurrentCallbacks = 0;

        await CreateService().RunAsync(async _ =>
        {
            var current = Interlocked.Increment(ref activeCallbacks);
            int observed;
            while (current > (observed = Volatile.Read(ref maxConcurrentCallbacks)) &&
                   Interlocked.CompareExchange(ref maxConcurrentCallbacks, current, observed) != observed)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(125));
            if (Interlocked.Increment(ref callbacks) >= 3)
            {
                cancellation.Cancel();
            }

            Interlocked.Decrement(ref activeCallbacks);
        }, cancellation.Token);

        Assert.AreEqual(3, callbacks);
        Assert.AreEqual(1, maxConcurrentCallbacks);
    }

    [TestMethod]
    public async Task Run_DetectsNewJobWithoutRestart()
    {
        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var initial = await harness.NextAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, initial.TotalCount);

        var jobId = "job_new_0001";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));

        var discovered = await harness.WaitForAsync(
            snapshot => snapshot.Jobs.Any(job => job.JobId == jobId),
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, discovered.TotalCount);
        Assert.AreEqual(ForgeJobStatus.Unknown, discovered.Jobs.Single().Status);
        Assert.AreEqual(MonitoringState.Idle, discovered.State);
    }

    [TestMethod]
    public async Task Run_ExplicitRunningMetadata_ConfirmsMonitoringState()
    {
        var jobId = "job_running_0001";
        _workspace.WriteMetadata(jobId, status: "RUNNING");
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(MonitoringState.Monitoring, snapshot.State);
        Assert.AreEqual(1, snapshot.ActiveCount);
        Assert.AreEqual(ForgeJobStatus.Running, snapshot.Jobs[0].Status);
    }

    [TestMethod]
    public async Task Run_StatusTransition_UpdatesMonitoringState()
    {
        var jobId = "job_transition_0001";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var initial = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(MonitoringState.Idle, initial.State);

        _workspace.WriteMetadata(jobId, status: "RUNNING");
        var running = await harness.WaitForAsync(
            candidate => candidate.State == MonitoringState.Monitoring,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, running.ActiveCount);
        Assert.AreEqual(ForgeJobStatus.Running, running.Jobs[0].Status);
    }

    [TestMethod]
    public async Task Run_MissingStatusAndPassedDeadline_RemainsUnknown()
    {
        var jobId = "job_deadline_0001";
        _workspace.WriteMetadata(
            jobId,
            started: TestWorkspace.Timestamp("2026-01-01T00:00:00Z"),
            deadline: TestWorkspace.Timestamp("2026-01-01T00:15:00Z"));
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(TestWorkspace.Timestamp("2026-01-01T00:10:00Z")));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(ForgeJobStatus.Unknown, snapshot.Jobs[0].Status);
        Assert.IsTrue(snapshot.Jobs[0].DeadlinePassed);
        Assert.AreEqual(MonitoringState.Idle, snapshot.State);
        Assert.AreEqual(1, snapshot.UnknownCount);
    }

    [TestMethod]
    public async Task Run_DuplicateLogLines_ProduceOneTimelineEvent()
    {
        var jobId = "job_dupe_0001";
        _workspace.WriteMetadata(jobId);
        var line = TestWorkspace.StepStart(1_800_000_000_000);
        _workspace.WriteLog(jobId, line, line);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, snapshot.Jobs[0].ParsedEventCount);
        Assert.AreEqual(1, snapshot.Jobs[0].Events.Count);
    }

    [TestMethod]
    public async Task Run_TwoJobsSharingSession_RemainDistinct()
    {
        const string sharedSession = "ses_shared_0001";
        _workspace.WriteMetadata("job_shared_a");
        _workspace.WriteMetadata("job_shared_b");
        _workspace.WriteLog(
            "job_shared_a",
            TestWorkspace.StepStart(1_800_000_000_000, session: sharedSession, part: "a1"));
        _workspace.WriteLog(
            "job_shared_b",
            TestWorkspace.StepStart(1_800_000_000_500, session: sharedSession, part: "b1"));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 2,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, snapshot.Jobs.Select(job => job.JobId).Distinct().Count());
        Assert.IsTrue(snapshot.Jobs.All(job => job.SessionId == sharedSession));
    }

    [TestMethod]
    public async Task Run_LogReplacement_DoesNotDuplicateTimelineEvents()
    {
        var jobId = "job_replace_service_0001";
        _workspace.WriteMetadata(jobId);
        var firstLine = TestWorkspace.StepStart(1_800_000_000_000, part: "p1");
        _workspace.WriteLog(jobId, firstLine);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var initial = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 && candidate.Jobs[0].Events.Count == 1,
            TimeSpan.FromSeconds(5));

        var replacement = Path.Combine(_workspace.RunsRoot, "replacement-service.tmp");
        File.WriteAllText(
            replacement,
            firstLine + "\n" + TestWorkspace.Text(1_800_000_000_500, "new output", part: "p2") + "\n");
        File.Move(replacement, _workspace.LogPath(jobId), overwrite: true);

        var updated = await harness.WaitForAsync(
            candidate => candidate.Jobs.Count == 1 && candidate.Jobs[0].Events.Count == 2,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, updated.Jobs[0].Events.Count);
        Assert.AreEqual(2, updated.Jobs[0].Events.Select(item => item.EventKey).Distinct().Count());
        Assert.AreEqual(2, updated.Jobs[0].ParsedEventCount);
    }

    [TestMethod]
    public async Task Run_LargeBatch_LogsAllEventsWhileSnapshotRetainsNewest500()
    {
        const int eventCount = 800;
        var jobId = "job_large_service_0001";
        _workspace.WriteMetadata(jobId);
        var lines = new string[eventCount];
        for (var index = 0; index < eventCount; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "p" + index,
                message: "m" + index);
        }

        _workspace.WriteLog(jobId, lines);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.Jobs.Count == 1 &&
                         candidate.Jobs[0].ParsedEventCount == eventCount,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(eventCount, snapshot.Jobs[0].ParsedEventCount);
        Assert.AreEqual(500, snapshot.Jobs[0].Events.Count);

        await harness.StopAsync();

        var jsonlPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.jsonl",
            SearchOption.AllDirectories).Single();
        var logEventCount = File.ReadAllLines(jsonlPath)
            .Select(line => JsonDocument.Parse(line))
            .Count(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("kind").GetString() == "log_event";
                }
            });

        Assert.AreEqual(eventCount, logEventCount);
    }

    [TestMethod]
    public async Task Run_LoggingFailure_DoesNotStopMonitoring()
    {
        var jobId = "job_nolog_0001";
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));

        var service = new ForgeMonitoringService(
            _workspace.RunsRoot,
            new ThrowingLoggerFactory(),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)));

        await using var harness = new MonitoringHarness();
        harness.Start(service);
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, snapshot.TotalCount);
        Assert.IsTrue(snapshot.Warnings.Any(warning =>
            warning.Contains("log could not be created", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Run_MissingRunsDirectory_IsIdleAndAvailableFalse()
    {
        var missingRoot = Path.Combine(_workspace.Root, "no-runs-here");
        var service = new ForgeMonitoringService(
            missingRoot,
            new MonitoringRunLoggerFactory(_workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)));

        await using var harness = new MonitoringHarness();
        harness.Start(service);
        var snapshot = await harness.NextAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MonitoringState.Idle, snapshot.State);
        Assert.IsFalse(snapshot.ForgeDirectoryAvailable);
        Assert.AreEqual(0, snapshot.TotalCount);
        Assert.IsTrue(snapshot.Warnings.Count > 0);
    }

    [TestMethod]
    public async Task Run_MalformedMetadata_DoesNotCrashAndIsUnknown()
    {
        _workspace.WriteRawMetadata("job_bad_service_0001.json", "{ not valid");

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(ForgeJobStatus.Unknown, snapshot.Jobs[0].Status);
        Assert.AreEqual(MonitoringState.Idle, snapshot.State);
    }

    [TestMethod]
    public async Task Run_ReadOnly_DoesNotModifyForgeFiles()
    {
        var jobId = "job_readonly_0001";
        var metadataPath = _workspace.WriteMetadata(jobId);
        var logPath = _workspace.LogPath(jobId);
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));
        var metadataBytes = File.ReadAllBytes(metadataPath);
        var logBytes = File.ReadAllBytes(logPath);
        var metadataWriteTime = File.GetLastWriteTimeUtc(metadataPath);
        var logWriteTime = File.GetLastWriteTimeUtc(logPath);
        var filesBefore = Directory.GetFiles(_workspace.RunsRoot).OrderBy(path => path).ToArray();

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        await harness.WaitForAsync(candidate => candidate.TotalCount == 1, TimeSpan.FromSeconds(5));
        await harness.StopAsync();

        CollectionAssert.AreEqual(metadataBytes, File.ReadAllBytes(metadataPath));
        CollectionAssert.AreEqual(logBytes, File.ReadAllBytes(logPath));
        Assert.AreEqual(metadataWriteTime, File.GetLastWriteTimeUtc(metadataPath));
        Assert.AreEqual(logWriteTime, File.GetLastWriteTimeUtc(logPath));
        CollectionAssert.AreEqual(filesBefore, Directory.GetFiles(_workspace.RunsRoot).OrderBy(path => path).ToArray());
    }

    [TestMethod]
    public async Task Run_RepeatedLifecycle_CreatesUniqueLogsAndClosesThem()
    {
        for (var run = 0; run < 2; run++)
        {
            await using var harness = new MonitoringHarness();
            harness.Start(CreateService());
            await harness.NextAsync(TimeSpan.FromSeconds(5));
            await harness.StopAsync();
        }

        var logs = Directory.GetFiles(_workspace.LogsRoot, "*.log", SearchOption.AllDirectories);
        Assert.AreEqual(2, logs.Length);
        Assert.AreEqual(2, logs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(logs.All(CanOpenExclusively));
    }

    [TestMethod]
    public async Task Run_LogReplacement_DoesNotDoubleCountCost()
    {
        var jobId = "job_cost_replacement_0001";
        var firstLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.200,
            session: "ses_cost_replacement",
            part: "finish-replaced",
            message: "msg-replaced");
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, firstLine);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var beforeReplacement = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 1,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(0.200, beforeReplacement.Jobs[0].ObservedCost!.Value, 1e-12);

        var replacement = Path.Combine(_workspace.RunsRoot, "replacement-cost.tmp");
        File.WriteAllText(
            replacement,
            firstLine + "\n" + TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.300,
                session: "ses_cost_replacement",
                part: "finish-new",
                message: "msg-new") + "\n");
        File.Move(replacement, _workspace.LogPath(jobId), overwrite: true);

        var afterReplacement = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > beforeReplacement.ObservedAt &&
                         candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 2,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, afterReplacement.Jobs[0].Events.Count);
        Assert.AreEqual(0.500, afterReplacement.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_replacement", afterReplacement.Jobs[0].SessionId);

        await harness.StopAsync();
        Assert.AreEqual(2, CountLoggedJsonlEvents(jobId));
        Assert.AreEqual(2, CountLoggedTextEvents(jobId));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, 1_800_000_001_000));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, 1_800_000_002_000));
    }

    [TestMethod]
    public async Task Run_CostBearingDuplicateAndDistinctIncrementalEvents_UpdateCostOnce()
    {
        var jobId = "job_cost_incremental_0001";
        var firstLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.125,
            session: "ses_cost_incremental",
            part: "finish-1",
            message: "msg-1");
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, firstLine);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var first = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 1 &&
                         candidate.Jobs[0].Events.Count == 1,
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(0.125, first.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_incremental", first.Jobs[0].SessionId);

        var duplicate = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > first.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, duplicate.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.125, duplicate.Jobs[0].ObservedCost!.Value, 1e-12);

        _workspace.AppendLog(jobId, firstLine);
        var repeated = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > duplicate.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, repeated.Jobs[0].ParsedEventCount);
        Assert.AreEqual(1, repeated.Jobs[0].Events.Count);
        Assert.AreEqual(0.125, repeated.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_incremental", repeated.Jobs[0].SessionId);

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.250,
                session: "ses_cost_incremental",
                part: "finish-2",
                message: "msg-2"));
        var distinct = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > repeated.ObservedAt &&
                         candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 2,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, distinct.Jobs[0].Events.Count);
        Assert.AreEqual(0.375, distinct.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_incremental", distinct.Jobs[0].SessionId);

        await harness.StopAsync();
        Assert.AreEqual(2, CountLoggedJsonlEvents(jobId));
        Assert.AreEqual(2, CountLoggedTextEvents(jobId));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, 1_800_000_001_000));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, 1_800_000_002_000));
    }

    [TestMethod]
    public async Task Run_CostBearingReplayAfterTimelineExceedsCapacity_KeepsCostAndSession()
    {
        var jobId = "job_cost_replay_capacity_0001";
        var highTimestamp = 1_800_000_900_000;
        var highLine = TestWorkspace.StepFinish(
            highTimestamp,
            0.500,
            session: "ses_cost_capacity",
            part: "cost-retained",
            message: "msg-cost-retained");
        var lines = new string[502];
        lines[0] = highLine;
        for (var index = 1; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                session: "ses_cost_capacity",
                part: "capacity-" + index.ToString("0000"),
                message: "msg-capacity-" + index.ToString("0000"));
        }

        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var beforeReplay = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == lines.Length &&
                         candidate.Jobs[0].Events.Count == 500,
            TimeSpan.FromSeconds(20));

        Assert.AreEqual(0.500, beforeReplay.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_capacity", beforeReplay.Jobs[0].SessionId);
        Assert.AreEqual(
            1,
            beforeReplay.Jobs[0].Events.Count(item =>
                item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(highTimestamp)));

        _workspace.AppendLog(jobId, highLine);
        var firstAfterReplay = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > beforeReplay.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(10));
        var afterReplay = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > firstAfterReplay.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(lines.Length, afterReplay.Jobs[0].ParsedEventCount);
        Assert.AreEqual(500, afterReplay.Jobs[0].Events.Count);
        Assert.AreEqual(0.500, afterReplay.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual("ses_cost_capacity", afterReplay.Jobs[0].SessionId);
        Assert.AreEqual(
            1,
            afterReplay.Jobs[0].Events.Count(item =>
                item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(highTimestamp)));

        await harness.StopAsync();
        Assert.AreEqual(lines.Length, CountLoggedJsonlEvents(jobId));
        Assert.AreEqual(lines.Length, CountLoggedTextEvents(jobId));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, highTimestamp));
    }

    [TestMethod]
    public async Task Run_CostBearingReplayAfterRecentDedupeExpires_KeepsCostCountAndSession()
    {
        const int continuationEvents = 10_001;
        var jobId = "job_cost_replay_expiry_0001";
        var highTimestamp = 1_800_100_000_000;
        var highLine = TestWorkspace.StepFinish(
            highTimestamp,
            0.750,
            session: "ses_cost_original",
            part: "cost-original",
            message: "msg-cost-original");
        var lines = new string[continuationEvents + 1];
        lines[0] = highLine;
        for (var index = 1; index < lines.Length; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                session: "ses_cost_continued",
                part: "continuation-" + index.ToString("0000"),
                message: "msg-continuation-" + index.ToString("0000"));
        }

        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateService());
        var beforeReplay = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == lines.Length &&
                         candidate.Jobs[0].Events.Count == 500 &&
                         candidate.Jobs[0].SessionId == "ses_cost_continued",
            TimeSpan.FromSeconds(40));

        Assert.AreEqual(0.750, beforeReplay.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual(
            1,
            beforeReplay.Jobs[0].Events.Count(item =>
                item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(highTimestamp)),
            "the high-timestamp cost event must remain retained");

        _workspace.AppendLog(jobId, highLine);
        var firstAfterReplay = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > beforeReplay.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(20));
        var afterReplay = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > firstAfterReplay.ObservedAt &&
                         candidate.TotalCount == 1,
            TimeSpan.FromSeconds(20));

        Assert.AreEqual(lines.Length, afterReplay.Jobs[0].ParsedEventCount);
        Assert.AreEqual(500, afterReplay.Jobs[0].Events.Count);
        Assert.AreEqual(
            beforeReplay.Jobs[0].ObservedCost!.Value,
            afterReplay.Jobs[0].ObservedCost!.Value,
            1e-12,
            "a rejected duplicate must not add cost");
        Assert.AreEqual("ses_cost_continued", afterReplay.Jobs[0].SessionId);
        Assert.AreEqual(
            1,
            afterReplay.Jobs[0].Events.Count(item =>
                item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(highTimestamp)),
            "the replayed cost event must remain single in the retained timeline");

        await harness.StopAsync();
        Assert.AreEqual(lines.Length, CountLoggedJsonlEvents(jobId));
        Assert.AreEqual(lines.Length, CountLoggedTextEvents(jobId));
        Assert.AreEqual(1, CountLoggedJsonlEventTimestamp(jobId, highTimestamp));
    }

    [TestMethod]
    public async Task Run_DurableBootstrap_RestoresAggregatesAndTimelineAfterRestart()
    {
        var jobId = "job_b2_durable_restart_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(
            jobId,
            TestWorkspace.StepStart(1_800_000_000_000, session: "ses_b2_restart"),
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.75m,
                session: "ses_b2_restart",
                part: "restart-finish",
                message: "restart-msg"));

        await using (var firstHarness = new MonitoringHarness())
        {
            firstHarness.Start(CreateDurableService(databasePath));
            var first = await firstHarness.WaitForAsync(
                candidate => candidate.TotalCount == 1 &&
                             candidate.Jobs[0].ParsedEventCount == 2 &&
                             candidate.Jobs[0].ObservedCost == 0.75,
                TimeSpan.FromSeconds(10));
            Assert.AreEqual("ses_b2_restart", first.Jobs[0].SessionId);
            Assert.AreEqual(2, first.Jobs[0].Events.Count);
            await firstHarness.StopAsync();
        }

        await using (var secondHarness = new MonitoringHarness())
        {
            secondHarness.Start(CreateDurableService(databasePath));
            var restored = await secondHarness.WaitForAsync(
                candidate => candidate.TotalCount == 1 &&
                             candidate.Jobs[0].ParsedEventCount == 2 &&
                             candidate.Jobs[0].ObservedCost == 0.75,
                TimeSpan.FromSeconds(10));
            Assert.AreEqual("ses_b2_restart", restored.Jobs[0].SessionId);
            Assert.AreEqual(2, restored.Jobs[0].Events.Count);
            await secondHarness.StopAsync();
        }

        Assert.IsFalse(File.Exists(Path.Combine(_workspace.Root, "data", "events.db")));
    }

    [TestMethod]
    public async Task Run_DurableLogPathChange_PreservesAggregatesAndDeduplicatesCopy()
    {
        var jobId = "job_b2_durable_path_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        var originalPath = Path.Combine(_workspace.RunsRoot, "original-b2.log");
        var changedPath = Path.Combine(_workspace.RunsRoot, "changed-b2.log");
        var originalLine = TestWorkspace.StepFinish(
            1_800_000_001_000,
            0.25m,
            session: "ses_b2_path",
            part: "path-original",
            message: "path-msg-original");
        _workspace.WriteMetadata(jobId, logPath: originalPath);
        File.WriteAllText(originalPath, originalLine + "\n");

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(databasePath));
        var before = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 1 &&
                         candidate.Jobs[0].ObservedCost == 0.25,
            TimeSpan.FromSeconds(10));

        File.WriteAllText(
            changedPath,
            originalLine + "\n" + TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.30m,
                session: "ses_b2_path",
                part: "path-new",
                message: "path-msg-new") + "\n");
        _workspace.WriteMetadata(jobId, logPath: changedPath);

        var after = await harness.WaitForAsync(
            candidate => candidate.ObservedAt > before.ObservedAt &&
                         candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 2 &&
                         candidate.Jobs[0].ObservedCost == 0.55,
            TimeSpan.FromSeconds(10));
        Assert.AreEqual("ses_b2_path", after.Jobs[0].SessionId);
        Assert.AreEqual(2, after.Jobs[0].Events.Count);
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableLargeBootstrap_RetainsBoundedFiveHundredEventTimeline()
    {
        const int eventCount = 620;
        var jobId = "job_b2_durable_large_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        var lines = new string[eventCount];
        for (var index = 0; index < eventCount; index++)
        {
            lines[index] = TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "large-" + index.ToString("0000"),
                message: "msg-large-" + index.ToString("0000"));
        }

        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines);

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(
            databasePath,
            new MonitoringOptions(TimeSpan.FromMilliseconds(20))));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == eventCount,
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(eventCount, snapshot.Jobs[0].ParsedEventCount);
        Assert.AreEqual(500, snapshot.Jobs[0].Events.Count);
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableMultiJobBootstrap_ProgressesFairlyAndCancelsCleanly()
    {
        const int jobCount = 6;
        const int eventsPerJob = 120;
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        for (var job = 0; job < jobCount; job++)
        {
            var jobId = "job_b2_multi_" + job.ToString("000");
            _workspace.WriteMetadata(jobId);
            var lines = new string[eventsPerJob];
            for (var index = 0; index < eventsPerJob; index++)
            {
                lines[index] = TestWorkspace.StepStart(
                    1_800_000_000_000 + index,
                    session: "ses_b2_multi_" + job.ToString("000"),
                    part: "multi-" + job.ToString("000") + "-" + index.ToString("0000"),
                    message: "msg-multi-" + job.ToString("000") + "-" + index.ToString("0000"));
            }

            _workspace.WriteLog(jobId, lines);
        }

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(
            databasePath,
            new MonitoringOptions(TimeSpan.FromMilliseconds(20))));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == jobCount &&
                         candidate.Jobs.All(job => job.ParsedEventCount == eventsPerJob),
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(jobCount, snapshot.TotalCount);
        Assert.IsTrue(snapshot.Jobs.All(job => job.Events.Count <= 500));
        Assert.IsTrue(snapshot.Jobs.All(job =>
            job.Events.Count == Math.Min(eventsPerJob, 500)));
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableRestart_PreservesAmbiguityAndBothTimelineWindows()
    {
        var jobId = "job_b2_dual_restart_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        var lines = new List<string>
        {
            TestWorkspace.StepStart(
                1_800_000_900_000,
                part: "restart-high",
                message: "restart-high-msg")
        };
        for (var index = 1; index <= 502; index++)
        {
            lines.Add(TestWorkspace.StepStart(
                1_800_000_000_000 + index,
                part: "restart-" + index.ToString("0000"),
                message: "restart-msg-" + index.ToString("0000")));
        }

        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, lines.ToArray());

        await using (var firstHarness = new MonitoringHarness())
        {
            firstHarness.Start(CreateDurableService(databasePath));
            var before = await firstHarness.WaitForAsync(
                candidate => candidate.TotalCount == 1 &&
                             candidate.Jobs[0].ParsedEventCount == 503 &&
                             candidate.Jobs[0].Events.Count == 500,
                TimeSpan.FromSeconds(20));
            Assert.IsFalse(before.Jobs[0].HasObservedAmbiguousTimestamps);
            Assert.IsTrue(before.Jobs[0].Events.Any(item =>
                item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
            await firstHarness.StopAsync();
        }

        await using var secondHarness = new MonitoringHarness();
        secondHarness.Start(CreateDurableService(databasePath));
        var restored = await secondHarness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 503 &&
                         candidate.Jobs[0].Events.Count == 500,
            TimeSpan.FromSeconds(20));
        Assert.IsTrue(restored.Jobs[0].Events.Any(item =>
            item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));

        _workspace.AppendLog(
            jobId,
            TestWorkspace.StepStartWithoutTimestamp(
                part: "restart-missing",
                message: "restart-missing-msg"));

        var ambiguous = await secondHarness.WaitForAsync(
            candidate => candidate.ObservedAt > restored.ObservedAt &&
                         candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 504 &&
                         candidate.Jobs[0].HasObservedAmbiguousTimestamps,
            TimeSpan.FromSeconds(20));
        Assert.AreEqual(500, ambiguous.Jobs[0].Events.Count);
        Assert.IsFalse(ambiguous.Jobs[0].Events.Any(item =>
            item.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_900_000)));
        await secondHarness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableRecordTooLarge_MarksIncompleteCoverage()
    {
        var jobId = "job_b2_record_too_large_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _workspace.WriteMetadata(jobId);
        File.WriteAllText(_workspace.LogPath(jobId), new string('a', 2048));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(
            databasePath,
            new MonitoringOptions(TimeSpan.FromMilliseconds(20)),
            new EventLedgerOptions
            {
                MaxBytesPerTransaction = 512,
                MaxLineBytes = 512
            }));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].HasIncompleteCoverage &&
                         candidate.Jobs[0].Warnings.Any(warning =>
                             warning.Contains("bounded", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, snapshot.Jobs[0].ParsedEventCount);
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableNonPrimaryUnavailable_MarksIncompleteCoverage()
    {
        var jobId = "job_b2_nonprimary_unavailable_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        var originalPath = Path.Combine(_workspace.RunsRoot, "nonprimary-original.log");
        var changedPath = Path.Combine(_workspace.RunsRoot, "nonprimary-changed.log");
        var originalLine = TestWorkspace.StepStart(
            1_800_000_001_000,
            part: "nonprimary-original",
            message: "nonprimary-original-msg");
        _workspace.WriteMetadata(jobId, logPath: originalPath);
        File.WriteAllText(originalPath, originalLine + "\n");

        await using (var firstHarness = new MonitoringHarness())
        {
            firstHarness.Start(CreateDurableService(databasePath));
            await firstHarness.WaitForAsync(
                candidate => candidate.TotalCount == 1 &&
                             candidate.Jobs[0].ParsedEventCount == 1,
                TimeSpan.FromSeconds(10));
            await firstHarness.StopAsync();
        }

        File.WriteAllText(
            changedPath,
            originalLine + "\n" + TestWorkspace.StepStart(
                1_800_000_002_000,
                part: "nonprimary-new",
                message: "nonprimary-new-msg") + "\n");
        _workspace.WriteMetadata(jobId, logPath: changedPath);
        File.Delete(originalPath);

        await using var secondHarness = new MonitoringHarness();
        secondHarness.Start(CreateDurableService(databasePath));
        var snapshot = await secondHarness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 2 &&
                         candidate.Jobs[0].HasIncompleteCoverage &&
                         candidate.Jobs[0].Warnings.Any(warning =>
                             warning.Contains("previously registered source", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(2, snapshot.Jobs[0].ParsedEventCount);
        await secondHarness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableProvisionalMetadata_MarksIncompleteCoverage()
    {
        var jobId = "job_provisional_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _workspace.WriteRawMetadata(jobId + ".json", "{ definitely broken");
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(databasePath));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].HasIncompleteCoverage &&
                         candidate.Jobs[0].Warnings.Any(warning =>
                             warning.Contains("provisional", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, snapshot.Jobs[0].ParsedEventCount);
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableUnassignedLog_ReportsIncompleteDiscovery()
    {
        var jobId = "job_b2_unassigned_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(jobId, TestWorkspace.StepStart(1_800_000_000_000));
        File.WriteAllText(
            Path.Combine(_workspace.RunsRoot, "orphan-unassigned.log"),
            "{}\n");

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(databasePath));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Warnings.Any(warning =>
                             warning.Contains("unassigned", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.IsTrue(snapshot.Warnings.Any(warning =>
            warning.Contains("discovery coverage is incomplete", StringComparison.OrdinalIgnoreCase)));
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableCustomMetadataPath_ReportsFallbackUncoveredWithoutIngestingIt()
    {
        var jobId = "job_b2_custom_fallback_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        var customPath = Path.Combine(_workspace.RunsRoot, "custom-cost.log");
        _workspace.WriteMetadata(jobId, logPath: customPath);
        File.WriteAllText(
            customPath,
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.25m,
                session: "ses_custom_fallback",
                part: "custom-part",
                message: "custom-message") + "\n");
        _workspace.WriteLog(
            jobId,
            TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.75m,
                session: "ses_custom_fallback",
                part: "fallback-part",
                message: "fallback-message"));

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(databasePath));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].ParsedEventCount == 1 &&
                         candidate.Jobs[0].ObservedCost == 0.25 &&
                         candidate.Jobs[0].HasIncompleteCoverage &&
                         candidate.Jobs[0].Warnings.Any(warning =>
                             warning.Contains("fallback", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(0.25, snapshot.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.AreEqual(1, snapshot.Jobs[0].ParsedEventCount);
        Assert.AreEqual(1, snapshot.Jobs[0].Events.Count);
        await harness.StopAsync();
    }

    [TestMethod]
    public async Task Run_DurableMissingIdentity_ReportsIncompleteCoverage()
    {
        var jobId = "job_b2_durable_missing_identity_0001";
        var databasePath = Path.Combine(_workspace.Root, "data", "events-v2.db");
        _workspace.WriteMetadata(jobId);
        _workspace.WriteLog(
            jobId,
            """{"type":"server_connected","timestamp":1800000000000}""");

        await using var harness = new MonitoringHarness();
        harness.Start(CreateDurableService(databasePath));
        var snapshot = await harness.WaitForAsync(
            candidate => candidate.TotalCount == 1 &&
                         candidate.Jobs[0].Warnings.Any(warning =>
                             warning.Contains("message/part", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, snapshot.Jobs[0].ParsedEventCount);
        Assert.IsNull(snapshot.Jobs[0].ObservedCost);
        await harness.StopAsync();
    }

    private ForgeMonitoringService CreateDurableService(
        string databasePath,
        MonitoringOptions? options = null,
        EventLedgerOptions? ledgerOptions = null) =>
        new(
            _workspace.RunsRoot,
            new MonitoringRunLoggerFactory(_workspace.LogsRoot),
            options ?? new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => DateTimeOffset.UtcNow,
            () => EventLedger.OpenProjection(
                databasePath,
                _workspace.Root,
                ledgerOptions));

    private int CountLoggedJsonlEvents(string jobId)
    {
        var jsonlPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.jsonl",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(jsonlPath)
            .Select(line => JsonDocument.Parse(line))
            .Count(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("kind").GetString() == "log_event" &&
                           document.RootElement.GetProperty("jobId").GetString() == jobId;
                }
            });
    }

    private int CountLoggedJsonlEventTimestamp(string jobId, long timestamp)
    {
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var jsonlPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.jsonl",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(jsonlPath)
            .Select(line => JsonDocument.Parse(line))
            .Count(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("kind").GetString() == "log_event" &&
                           document.RootElement.GetProperty("jobId").GetString() == jobId &&
                           document.RootElement.TryGetProperty("eventTs", out var eventTs) &&
                           eventTs.GetDateTimeOffset() == expected;
                }
            });
    }

    private int CountLoggedTextEvents(string jobId)
    {
        var textPath = Directory.GetFiles(
            _workspace.LogsRoot,
            "*.log",
            SearchOption.AllDirectories).Single();
        return File.ReadAllLines(textPath)
            .Count(line =>
                line.Contains("[" + jobId + "]", StringComparison.Ordinal) &&
                (line.Contains("Session step started", StringComparison.Ordinal) ||
                 line.Contains("Step finished", StringComparison.Ordinal)));
    }

    private ForgeMonitoringService CreateService() =>
        new(
            _workspace.RunsRoot,
            new MonitoringRunLoggerFactory(_workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            () => DateTimeOffset.UtcNow);

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class ThrowingLoggerFactory : IMonitoringLoggerFactory
    {
        public IMonitoringLogger Create(DateTimeOffset startedAt) =>
            throw new IOException("Simulated log failure.");
    }

    private sealed class MonitoringHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<MonitoringSnapshot> _snapshots =
            Channel.CreateUnbounded<MonitoringSnapshot>();

        public Task? Task { get; private set; }

        public void Start(ForgeMonitoringService service)
        {
            Task = System.Threading.Tasks.Task.Run(async () =>
            {
                await service.RunAsync(async snapshot =>
                {
                    await _snapshots.Writer.WriteAsync(snapshot, _cts.Token);
                }, _cts.Token);
            });
        }

        public async Task<MonitoringSnapshot> NextAsync(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _cts.Token,
                timeoutCts.Token);
            try
            {
                return await _snapshots.Reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException("No monitoring snapshot arrived before the timeout.");
            }
        }

        public async Task<MonitoringSnapshot> WaitForAsync(
            Func<MonitoringSnapshot, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("The expected monitoring snapshot did not arrive.");
                }

                var snapshot = await NextAsync(remaining);
                if (predicate(snapshot))
                {
                    return snapshot;
                }
            }
        }

        public async Task StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            if (Task is not null)
            {
                try
                {
                    await Task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during cancellation.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts.Dispose();
            _snapshots.Writer.TryComplete();
        }
    }
}
