using System.Globalization;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Synthetic-only V3 runtime composition regressions. No production database,
/// real forge log, or OpenCode history is opened.
/// </summary>
[TestClass]
public sealed class V3MonitoringCompositionTests
{
    private TestWorkspace _workspace = null!;
    private string _dataRoot = null!;
    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _dataRoot = Path.Combine(_workspace.Root, "data");
        _databasePath = Path.Combine(_dataRoot, "events-v3.db");
        Directory.CreateDirectory(_dataRoot);
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void ProductionComposition_SelectsOnlyTheV3PathAndRoot()
    {
        var composition = MonitoringComposition.CreateProduction();

        Assert.AreEqual(
            LedgerStoragePaths.DefaultV3DatabasePath,
            composition.Options.DatabasePath);
        Assert.AreEqual(
            LedgerStoragePaths.DefaultDataDirectory,
            composition.Options.DatabaseRoot);
        StringAssert.EndsWith(composition.Options.DatabasePath, "events-v3.db");
        Assert.AreNotEqual(
            Path.Combine(composition.Options.DatabaseRoot, "events.db"),
            composition.Options.DatabasePath);
        Assert.AreNotEqual(
            Path.Combine(composition.Options.DatabaseRoot, "events-v2.db"),
            composition.Options.DatabasePath);

        var service = composition.CreateService();
        Assert.AreEqual(composition.Options.RunsRoot, service.RunsRoot);
        Assert.AreEqual(TimeSpan.FromSeconds(1), composition.Options.RefreshInterval);
        Assert.AreEqual(composition.Options.RefreshInterval, service.RefreshInterval);
    }

    [TestMethod]
    public void IsolatedComposition_AllPathsAreRequired_AndIncompleteConfigNeverFallsBack()
    {
        var complete = IsolatedEnvironment();
        Assert.IsTrue(
            MonitoringComposition.TryCreateIsolated(complete, out var composition, out var error),
            error);
        Assert.IsNotNull(composition);
        Assert.AreEqual(TimeSpan.FromSeconds(1), composition!.Options.RefreshInterval);
        Assert.AreEqual(Path.GetFullPath(_workspace.RunsRoot), composition.Options.RunsRoot);
        Assert.AreEqual(Path.GetFullPath(_workspace.LogsRoot), composition.Options.LogsRoot);
        Assert.AreEqual(Path.GetFullPath(_databasePath), composition.Options.DatabasePath);
        Assert.AreEqual(Path.GetFullPath(_dataRoot), composition.Options.DatabaseRoot);

        var incomplete = new Dictionary<string, string?>
        {
            [MonitoringComposition.IsolatedRunsRootVariable] = _workspace.RunsRoot
        };
        Assert.IsFalse(
            MonitoringComposition.TryCreateIsolated(incomplete, out var partial, out var partialError));
        Assert.IsNull(partial);
        StringAssert.Contains(partialError, MonitoringComposition.IsolatedLogsRootVariable);
        Assert.ThrowsException<InvalidOperationException>(
            () => MonitoringComposition.CreateForStartup(incomplete));

        var outside = new Dictionary<string, string?>(complete)
        {
            [MonitoringComposition.IsolatedDatabasePathVariable] =
                Path.Combine(_workspace.Root, "outside", "events-v3.db")
        };
        Assert.IsFalse(
            MonitoringComposition.TryCreateIsolated(outside, out var rejected, out var rejectedError));
        Assert.IsNull(rejected);
        StringAssert.Contains(rejectedError, "database path");

        var misspelled = new Dictionary<string, string?>
        {
            ["DSMONITOR_ISOLATED_RUNS_ROOTS"] = _workspace.RunsRoot
        };
        Assert.ThrowsException<InvalidOperationException>(
            () => MonitoringComposition.CreateForStartup(misspelled));

        var production = MonitoringComposition.CreateForStartup(
            new Dictionary<string, string?>());
        Assert.AreEqual(
            LedgerStoragePaths.DefaultV3DatabasePath,
            production.Options.DatabasePath);
    }

    [TestMethod]
    public async Task FreshV3Service_IngestsRestartsAndPreservesExactDecimalCost()
    {
        _workspace.WriteMetadata("job_v3_service");
        _workspace.WriteLog(
            "job_v3_service",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.00125m,
                session: "ses_v3_service",
                part: "v3-part-a",
                message: "v3-message-a"),
            TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.000075m,
                session: "ses_v3_service",
                part: "v3-part-b",
                message: "v3-message-b"));

        var composition = CreateComposition();
        var first = await ScanOnce(composition);
        Assert.AreEqual(1, first.Jobs.Count);
        Assert.AreEqual(2, first.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.001325, first.Jobs[0].ObservedCost!.Value, 1e-12);

        Assert.IsTrue(File.Exists(_databasePath));
        Assert.AreEqual(3L, Scalar(_databasePath, "PRAGMA user_version;"));
        Assert.AreEqual("3", TextScalar(
            _databasePath,
            "SELECT value FROM ledger_meta WHERE key = 'schema_version';"));
        Assert.IsFalse(File.Exists(Path.Combine(_dataRoot, "events.db")));
        Assert.IsFalse(File.Exists(Path.Combine(_dataRoot, "events-v2.db")));

        using (var ledger = EventLedger.OpenV3Projection(_databasePath, _dataRoot))
        {
            var aggregate = ledger.GetJobAggregate("job_v3_service");
            Assert.AreEqual(2L, aggregate.LogicalEventCount);
            Assert.AreEqual(0.001325m, aggregate.CostTotal);
            Assert.AreEqual("ses_v3_service", aggregate.LatestSessionId);
        }

        var restarted = await ScanOnce(composition);
        Assert.AreEqual(2, restarted.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.001325, restarted.Jobs[0].ObservedCost!.Value, 1e-12);

        using (var ledger = EventLedger.OpenV3Projection(_databasePath, _dataRoot))
        {
            Assert.AreEqual(2L, ledger.GetJobAggregate("job_v3_service").LogicalEventCount);
            Assert.AreEqual(0.001325m, ledger.GetJobAggregate("job_v3_service").CostTotal);
        }
    }

    [TestMethod]
    public async Task SharedSessionV3Jobs_KeepDistinctOwnershipCostsAndLiveActivity()
    {
        _workspace.WriteMetadata("job_shared_a");
        _workspace.WriteMetadata("job_shared_b");
        _workspace.WriteLog(
            "job_shared_a",
            AssistantText("msg-a", "alpha result", 1_800_000_001_000),
            AssistantFinish("msg-a", 0.25m, 1_800_000_002_000));
        _workspace.WriteLog(
            "job_shared_b",
            AssistantText("msg-b", "bravo result", 1_800_000_003_000),
            AssistantFinish("msg-b", 0.25m, 1_800_000_004_000));

        var snapshot = await ScanOnce(CreateComposition());
        Assert.AreEqual(2, snapshot.Jobs.Count);
        foreach (var job in snapshot.Jobs)
        {
            Assert.AreEqual("shared-session", job.SessionId);
            Assert.AreEqual(2, job.ParsedEventCount);
            Assert.AreEqual(0.25, job.ObservedCost!.Value, 1e-12);
        }

        var jobA = snapshot.Jobs.Single(job => job.JobId == "job_shared_a");
        var jobB = snapshot.Jobs.Single(job => job.JobId == "job_shared_b");
        Assert.AreEqual("alpha result", jobA.LiveActivity.Doing?.Text, jobA.LiveActivity.Availability);
        Assert.AreEqual("alpha result", jobA.LiveActivity.Done?.Text, jobA.LiveActivity.Availability);
        Assert.AreEqual("msg-a", jobA.LiveActivity.Done?.MessageId, jobA.LiveActivity.Availability);
        Assert.AreEqual("bravo result", jobB.LiveActivity.Doing?.Text, jobB.LiveActivity.Availability);
        Assert.AreEqual("bravo result", jobB.LiveActivity.Done?.Text, jobB.LiveActivity.Availability);
        Assert.AreEqual("msg-b", jobB.LiveActivity.Done?.MessageId, jobB.LiveActivity.Availability);

        using var ledger = EventLedger.OpenV3Projection(_databasePath, _dataRoot);
        Assert.AreEqual(2L, ledger.GetJobAggregate("job_shared_a").LogicalEventCount);
        Assert.AreEqual(2L, ledger.GetJobAggregate("job_shared_b").LogicalEventCount);
        Assert.AreEqual(0.25m, ledger.GetJobAggregate("job_shared_a").CostTotal);
        Assert.AreEqual(0.25m, ledger.GetJobAggregate("job_shared_b").CostTotal);
    }

    [TestMethod]
    public async Task InjectedV3CommitFailure_RollsBackAndRetainsCommittedAggregate()
    {
        _workspace.WriteMetadata("job_v3_rollback");
        _workspace.WriteLog(
            "job_v3_rollback",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.10m,
                session: "ses_v3_rollback",
                part: "rollback-a",
                message: "rollback-a"));

        var fail = false;
        var options = new EventLedgerOptions
        {
            BeforeCommitFaultInjector = () =>
            {
                if (fail)
                {
                    throw new IOException("Synthetic V3 commit failure.");
                }
            }
        };
        var composition = CreateComposition();

        var first = await ScanOnce(composition, options);
        Assert.AreEqual(1, first.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.10, first.Jobs[0].ObservedCost!.Value, 1e-12);

        _workspace.AppendLog(
            "job_v3_rollback",
            TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.20m,
                session: "ses_v3_rollback",
                part: "rollback-b",
                message: "rollback-b"));
        fail = true;
        var failed = await ScanOnce(composition, options);
        Assert.AreEqual(1, failed.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.10, failed.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.IsTrue(failed.Jobs[0].Warnings.Any(warning =>
            warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            warning.Contains("failed", StringComparison.OrdinalIgnoreCase)));

        using (var ledger = EventLedger.OpenV3Projection(_databasePath, _dataRoot))
        {
            Assert.AreEqual(1L, ledger.GetJobAggregate("job_v3_rollback").LogicalEventCount);
            Assert.AreEqual(0.10m, ledger.GetJobAggregate("job_v3_rollback").CostTotal);
        }

        fail = false;
        var recovered = await ScanOnce(composition, options);
        Assert.AreEqual(2, recovered.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.30, recovered.Jobs[0].ObservedCost!.Value, 1e-12);
    }

    [TestMethod]
    public async Task V3FailureToOpen_IsMetadataOnly_AndDoesNotCreateOrUseLegacyDatabases()
    {
        _workspace.WriteMetadata("job_metadata_only");
        _workspace.WriteLog(
            "job_metadata_only",
            TestWorkspace.StepFinish(1_800_000_001_000, 0.25m));

        var blockedRoot = Path.Combine(_workspace.Root, "blocked-data");
        File.WriteAllText(blockedRoot, "not a directory");
        var composition = new MonitoringComposition(new MonitoringCompositionOptions(
            _workspace.RunsRoot,
            _workspace.LogsRoot,
            Path.Combine(blockedRoot, "events-v3.db"),
            blockedRoot,
            TimeSpan.FromMilliseconds(50)));

        var snapshot = await ScanOnce(composition);
        Assert.AreEqual(1, snapshot.Jobs.Count);
        Assert.AreEqual(0, snapshot.Jobs[0].ParsedEventCount);
        Assert.IsNull(snapshot.Jobs[0].ObservedCost);
        Assert.IsNull(snapshot.Jobs[0].LiveActivity.Doing);
        Assert.IsTrue(snapshot.Warnings.Any(warning =>
            warning.Contains("Durable ledger could not be opened", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(snapshot.Jobs[0].Warnings.Any(warning =>
            warning.Contains("Durable aggregate state is unavailable", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(File.Exists(Path.Combine(_workspace.Root, "events.db")));
        Assert.IsFalse(File.Exists(Path.Combine(_workspace.Root, "events-v2.db")));
    }

    [TestMethod]
    public async Task V3Service_PreservesSyntheticV1V2DatabasesAndSidecars()
    {
        var v1 = Path.Combine(_dataRoot, "events.db");
        var v2 = Path.Combine(_dataRoot, "events-v2.db");
        var v1Wal = v1 + "-wal";
        var v1Shm = v1 + "-shm";
        var v2Wal = v2 + "-wal";
        var v2Shm = v2 + "-shm";
        var markers = new Dictionary<string, byte[]>
        {
            [v1] = new byte[] { 1, 3, 5, 7 },
            [v1Wal] = new byte[] { 2, 4, 6, 8 },
            [v1Shm] = new byte[] { 9, 10, 11 },
            [v2] = new byte[] { 12, 14, 16, 18 },
            [v2Wal] = new byte[] { 13, 15, 17, 19 },
            [v2Shm] = new byte[] { 20, 21, 22 }
        };
        foreach (var marker in markers)
        {
            File.WriteAllBytes(marker.Key, marker.Value);
        }

        _workspace.WriteMetadata("job_preserve_legacy");
        _workspace.WriteLog(
            "job_preserve_legacy",
            TestWorkspace.StepFinish(1_800_000_001_000, 0.125m));

        var snapshot = await ScanOnce(CreateComposition());
        Assert.AreEqual(1, snapshot.Jobs[0].ParsedEventCount);
        Assert.AreEqual(0.125, snapshot.Jobs[0].ObservedCost!.Value, 1e-12);
        Assert.IsTrue(File.Exists(_databasePath));

        foreach (var marker in markers)
        {
            CollectionAssert.AreEqual(marker.Value, File.ReadAllBytes(marker.Key), marker.Key);
        }
    }

    private MonitoringComposition CreateComposition() =>
        new(new MonitoringCompositionOptions(
            _workspace.RunsRoot,
            _workspace.LogsRoot,
            _databasePath,
            _dataRoot,
            TimeSpan.FromMilliseconds(50)));

    private Dictionary<string, string?> IsolatedEnvironment() => new()
    {
        [MonitoringComposition.IsolatedRunsRootVariable] = _workspace.RunsRoot,
        [MonitoringComposition.IsolatedLogsRootVariable] = _workspace.LogsRoot,
        [MonitoringComposition.IsolatedDatabasePathVariable] = _databasePath,
        [MonitoringComposition.IsolatedDatabaseRootVariable] = _dataRoot
    };

    private static async Task<MonitoringSnapshot> ScanOnce(
        MonitoringComposition composition,
        EventLedgerOptions? ledgerOptions = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        MonitoringSnapshot? result = null;
        await composition.CreateService(ledgerOptions).RunAsync(snapshot =>
        {
            result = snapshot;
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        Assert.IsNotNull(result);
        return result;
    }

    private static string AssistantText(string message, string text, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "text",
            sessionID = "shared-session",
            timestamp,
            role = "assistant",
            part = new
            {
                id = "text-" + message,
                messageID = message,
                type = "text",
                text
            }
        });

    private static string AssistantFinish(string message, decimal cost, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "step_finish",
            sessionID = "shared-session",
            timestamp,
            role = "assistant",
            part = new
            {
                id = "finish-" + message,
                messageID = message,
                type = "step-finish",
                reason = "stop",
                cost
            }
        });

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? TextScalar(string databasePath, string sql)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
