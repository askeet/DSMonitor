using System.Text;
using DSMonitor.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Deterministic, synthetic fixtures for the fresh V3 ledger profile. These
/// tests never open the B1/B2 databases with the V3 profile, never migrate a
/// database, and never use real forge/OpenCode data.
/// </summary>
[TestClass]
public sealed class V3LedgerProfileTests
{
    private const string JobId = "job_v3_0001";
    private const string Now = "2026-01-01T00:00:00Z";

    private TestWorkspace _workspace = null!;
    private string _databasePath = null!;
    private string _v1Path = null!;
    private string _v2Path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _databasePath = Path.Combine(_workspace.Root, "data", "events-v3.db");
        _v1Path = Path.Combine(_workspace.Root, "data", "events.db");
        _v2Path = Path.Combine(_workspace.Root, "data", "events-v2.db");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Fresh_Open_CreatesVersion3SchemaAndV3Path()
    {
        Assert.AreEqual(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DSMonitor",
                "Data",
                "events-v3.db"),
            LedgerStoragePaths.DefaultV3DatabasePath);
        StringAssert.EndsWith(LedgerStoragePaths.DefaultV3DatabasePath, "events-v3.db");
        Assert.AreEqual(3, EventLedgerSchema.V3Version);

        using (var ledger = CreateLedger())
        {
            Assert.AreEqual(Path.GetFullPath(_databasePath), ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        Assert.AreEqual(3L, Convert.ToInt64(Scalar(raw, "PRAGMA user_version;")));
        Assert.AreEqual("3", Scalar(raw, "SELECT value FROM ledger_meta WHERE key = 'schema_version';"));

        var expectedTables = new[]
        {
            "bootstrap_sources", "content_items", "cost_attributions",
            "delegation_invocations", "discovery_state", "display_overrides",
            "enrichment_imports", "event_observation_context",
            "execution_evidence", "execution_segments", "external_references",
            "invocation_jobs", "job_aggregates", "job_coverage",
            "job_logical_keys", "job_sources", "jobs", "ledger_meta",
            "logical_events", "messages", "monitoring_starts",
            "opencode_sessions", "project_paths", "projects", "provenance",
            "results", "run_enrichment", "run_segments", "segment_messages",
            "source_coverage", "source_cursors", "source_delivery_cuts",
            "source_events", "source_generations", "source_records",
            "task_runs", "tasks", "usage_observations", "validations"
        };
        var actualTables = QueryStrings(
            raw,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name <> 'sqlite_sequence' ORDER BY name;");
        CollectionAssert.AreEqual(expectedTables, actualTables);

        var jobColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('jobs') ORDER BY cid;");
        foreach (var column in new[]
                 {
                     "project_id", "display_name", "metadata_summary",
                     "reported_directory", "model", "started_utc",
                     "deadline_utc", "status", "provenance_id",
                     "runs_root", "provisional"
                 })
        {
            CollectionAssert.Contains(jobColumns, column);
        }

        var pathColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('project_paths') ORDER BY cid;");
        foreach (var column in new[]
                 {
                     "original_path", "normalized_path", "path_kind", "case_policy",
                     "is_active", "confirmed_utc"
                 })
        {
            CollectionAssert.Contains(pathColumns, column);
        }

        var messageColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('messages') ORDER BY cid;");
        CollectionAssert.Contains(messageColumns, "parent_message_id");

        var usageColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('usage_observations') ORDER BY cid;");
        foreach (var column in new[]
                 {
                     "source_instance_id", "source_usage_id", "usage_scope",
                     "authority", "provenance_id", "logical_event_id",
                     "logical_event_decision", "logical_event_cost_text",
                     "run_association_role", "invocation_run_id",
                     "invocation_run_association_role", "session_id", "message_id"
                 })
        {
            CollectionAssert.Contains(usageColumns, column);
        }
    }

    [TestMethod]
    public void Reopen_RestoresCommittedAggregateAndPhysicalState()
    {
        var logPath = _workspace.LogPath("job_v3_reopen");
        _workspace.WriteLog(
            "job_v3_reopen",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.25m,
                session: "ses_v3_reopen",
                part: "v3-reopen-part",
                message: "v3-reopen-message"));

        long generationId;
        long committedOffset;
        using (var ledger = CreateLedger())
        {
            var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
            generationId = result.GenerationId!.Value;
            committedOffset = result.CommittedOffset;
            Assert.AreEqual(1, result.Aggregate!.LogicalEventCount);
            Assert.AreEqual(0.25m, result.Aggregate.CostTotal);
        }

        using (var reopened = CreateLedger())
        {
            var aggregate = reopened.GetJobAggregate(JobId);
            Assert.AreEqual(1, aggregate.LogicalEventCount);
            Assert.AreEqual(0.25m, aggregate.CostTotal);
            Assert.AreEqual("ses_v3_reopen", aggregate.LatestSessionId);
            var generation = reopened.ListGenerations(JobId).Single();
            Assert.AreEqual(generationId, generation.GenerationId);
            Assert.AreEqual(committedOffset, generation.CommittedOffset);
            Assert.AreEqual(1, reopened.ListRecords(generationId).Count);
            Assert.AreEqual(1, reopened.ListEvents(generationId).Count);
        }
    }

    [TestMethod]
    public void VersionMismatch_IsRejectedWithoutMutationOrMigration()
    {
        var logPath = _workspace.LogPath("job_v3_version");
        _workspace.WriteLog("job_v3_version", TestWorkspace.StepStart(1_800_000_000_000));

        using (var v3 = CreateLedger())
        {
            Assert.AreEqual(LedgerIngestStatus.Committed,
                v3.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var v3Files = SnapshotFiles(_databasePath);
        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => EventLedger.OpenProjection(_databasePath, _workspace.Root));
        AssertPreserved(v3Files, _databasePath);

        using (var v2 = EventLedger.OpenProjection(_v2Path, _workspace.Root))
        {
            Assert.AreEqual(LedgerIngestStatus.Committed,
                v2.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var v2Files = SnapshotFiles(_v2Path);
        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => EventLedger.OpenV3Projection(_v2Path, _workspace.Root));
        AssertPreserved(v2Files, _v2Path);

        using (var v1 = new EventLedger(_v1Path, _workspace.Root))
        {
            Assert.AreEqual(LedgerIngestStatus.Committed,
                v1.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var v1Files = SnapshotFiles(_v1Path);
        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => EventLedger.OpenV3Projection(_v1Path, _workspace.Root));
        AssertPreserved(v1Files, _v1Path);
    }

    [TestMethod]
    public void PathGuard_RejectsDatabaseOutsideApprovedRoot()
    {
        var allowedRoot = Path.Combine(_workspace.Root, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var outsideDatabase = Path.Combine(_workspace.Root, "outside", "events-v3.db");

        Assert.ThrowsException<ArgumentException>(
            () => EventLedger.OpenV3Projection(outsideDatabase, allowedRoot));
        Assert.IsFalse(File.Exists(outsideDatabase));
    }

    [TestMethod]
    public void Manifest_ContainsNoUnexpectedViewsTriggersOrIndexes()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view';")));
        var actualTriggers = QueryStrings(
            raw,
            "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name;");
        CollectionAssert.AreEqual(
            new[]
            {
                "trg_invocations_validation_reviewer_insert",
                "trg_invocations_validation_reviewer_update",
                "trg_jobs_project_primary_consistency",
                "trg_jobs_project_primary_consistency_insert",
                "trg_jobs_provenance_subject_insert",
                "trg_jobs_provenance_subject_update",
                "trg_messages_parent_no_cycle_insert",
                "trg_messages_parent_no_cycle_update",
                "trg_provenance_subject_immutable",
                "trg_provenance_validation_reviewer_insert",
                "trg_provenance_validation_reviewer_update",
                "trg_sessions_parent_no_cycle_insert",
                "trg_sessions_parent_no_cycle_update",
                "trg_source_delivery_cuts_capture_immutable_update",
                "trg_source_delivery_cuts_capture_insert",
                "trg_task_runs_primary_project_insert",
                "trg_task_runs_primary_project_update",
                "trg_task_runs_validation_reviewer_insert",
                "trg_task_runs_validation_reviewer_update",
                "trg_tasks_project_primary_consistency",
                "trg_tasks_project_primary_consistency_insert",
                "trg_usage_provenance_subject_insert",
                "trg_usage_provenance_subject_update",
                "trg_validations_reviewer_provenance_insert",
                "trg_validations_reviewer_provenance_update"
            },
            actualTriggers,
            "Actual triggers: " + string.Join(", ", actualTriggers));

        var tables = QueryStrings(
            raw,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name <> 'sqlite_sequence' ORDER BY name;");
        Assert.AreEqual(39, tables.Count);
        Assert.IsTrue(tables.Contains("event_observation_context"));
        Assert.IsTrue(tables.Contains("source_delivery_cuts"));
        Assert.IsTrue(tables.Contains("cost_attributions"));

        var requiredIndexes = new[]
        {
            "ix_bootstrap_sources_job",
            "ix_content_items_kind",
            "ix_content_items_message",
            "ix_content_items_segment",
            "ix_cost_attributions_logical_event",
            "ix_cost_attributions_usage",
            "ix_delegation_invocations_run",
            "ix_enrichment_imports_imported",
            "ix_event_observation_context_event",
            "ix_event_observation_context_session",
            "ix_event_observation_context_start",
            "ix_execution_evidence_kind",
            "ix_execution_evidence_result",
            "ix_execution_segments_run",
            "ix_execution_segments_session",
            "ix_external_references_invocation",
            "ix_external_references_run",
            "ix_invocation_jobs_job",
            "ix_jobs_project",
            "ix_jobs_provenance",
            "ix_logical_events_job",
            "ix_logical_events_key",
            "ix_messages_parent",
            "ix_messages_session",
            "ix_monitoring_starts_started",
            "ix_opencode_sessions_parent",
            "ix_opencode_sessions_project",
            "ix_project_paths_alias",
            "ix_project_paths_normalized",
            "ix_project_paths_project",
            "ix_provenance_entity",
            "ix_provenance_import",
            "ix_results_invocation",
            "ix_results_run",
            "ix_run_enrichment_import",
            "ix_run_segments_segment",
            "ix_segment_messages_message",
            "ix_segment_messages_session",
            "ix_source_coverage_job",
            "ix_source_delivery_cuts_generation",
            "ix_source_delivery_cuts_start",
            "ix_source_events_job",
            "ix_source_generations_identity",
            "ix_source_records_generation",
            "ix_task_runs_job",
            "ix_task_runs_task",
            "ix_tasks_project",
            "ix_usage_observations_invocation",
            "ix_usage_observations_logical_event",
            "ix_usage_observations_message",
            "ix_usage_observations_provenance",
            "ix_usage_observations_run",
            "ix_usage_observations_session",
            "ix_usage_observations_source",
            "ix_validations_result",
            "ix_validations_run",
            "ix_validations_status",
            "ux_cost_attributions_active_event",
            "ux_cost_attributions_active_usage",
            "ux_delegation_invocations_id_run",
            "ux_display_overrides_invocation",
            "ux_display_overrides_project",
            "ux_display_overrides_run",
            "ux_display_overrides_session",
            "ux_display_overrides_task",
            "ux_enrichment_imports_id_status",
            "ux_event_observation_context_identity",
            "ux_execution_evidence_id_result",
            "ux_external_references_namespace",
            "ux_logical_events_id_decision_cost",
            "ux_project_paths_active_owner",
            "ux_provenance_identity",
            "ux_results_id_run",
            "ux_source_cursors_generation_offset",
            "ux_source_generations_id_job",
            "ux_task_runs_active_primary"
        };
        var actualIndexes = QueryStrings(
            raw,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT GLOB 'sqlite_autoindex_*' ORDER BY name;");
        Assert.AreEqual(
            requiredIndexes.Length,
            actualIndexes.Count,
            "Actual indexes: " + string.Join(", ", actualIndexes));
        CollectionAssert.AreEqual(requiredIndexes, actualIndexes);
    }

    [TestMethod]
    public void AlteredSchema_UnexpectedTable_IsRejectedAndPreserved()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using (var raw = OpenRawConnection(_databasePath))
        {
            Execute(raw, "CREATE TABLE unexpected_manifest (id INTEGER PRIMARY KEY);");
        }

        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => CreateLedger());

        using var verify = OpenRawConnection(_databasePath);
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'unexpected_manifest';")));
    }

    [TestMethod]
    public void AlteredSchema_DroppedExpectedIndex_IsRejectedAndPreserved()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using (var raw = OpenRawConnection(_databasePath))
        {
            Execute(raw, "DROP INDEX ix_messages_session;");
        }

        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => CreateLedger());

        using var verify = OpenRawConnection(_databasePath);
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_messages_session';")));
    }

    [TestMethod]
    public void UnexpectedTrigger_IsRejectedOnReopen()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using (var raw = OpenRawConnection(_databasePath))
        {
            Execute(
                raw,
                """
                CREATE TRIGGER silently_drop_messages
                BEFORE INSERT ON messages
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """);
        }

        AssertLedgerFailure(
            LedgerStorageFailure.IncompatibleSchema,
            () => CreateLedger());
    }

    [TestMethod]
    public void SameSessionConstraint_RejectsMismatchedMessageAndSegment()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertSession(raw, "inst", "ses_a");
        InsertSession(raw, "inst", "ses_b");
        InsertMessage(raw, "inst", "ses_a", "msg_a", 0);
        InsertMessage(raw, "inst", "ses_b", "msg_b", 0);
        InsertSegment(raw, "inst", "seg_a", "ses_a", runId: null);
        InsertSegment(raw, "inst", "seg_b", "ses_b", runId: null);

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO segment_messages(source_instance_id, segment_id, message_id, session_id, position) VALUES ('inst', 'seg_a', 'msg_b', 'ses_a', 0);"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO segment_messages(source_instance_id, segment_id, message_id, session_id, position) VALUES ('inst', 'seg_a', 'msg_b', 'ses_b', 0);"));

        Execute(
            raw,
            "INSERT INTO segment_messages(source_instance_id, segment_id, message_id, session_id, position) VALUES ('inst', 'seg_a', 'msg_a', 'ses_a', 0);");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM segment_messages;")));
    }

    [TestMethod]
    public void Jobs_ReportedMetadataAndProvenanceContractIsEnforced()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        Execute(
            raw,
            """
            INSERT INTO jobs(
                job_id, created_utc, reported_directory, model, started_utc,
                deadline_utc, status)
            VALUES (
                'job_meta', '2026-01-01T00:00:00Z', 'C:\work\reported',
                'deepseek/deepseek-flash', '2026-01-01T00:00:00Z',
                '2026-01-01T01:00:00Z', 'RUNNING');
            """);
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_meta', 'job_meta', 'forge', '{Now}');");
        Execute(raw, "UPDATE jobs SET provenance_id = 1 WHERE job_id = 'job_meta';");

        Assert.AreEqual(
            "C:\\work\\reported",
            Scalar(raw, "SELECT reported_directory FROM jobs WHERE job_id = 'job_meta';"));
        Assert.AreEqual(
            "deepseek/deepseek-flash",
            Scalar(raw, "SELECT model FROM jobs WHERE job_id = 'job_meta';"));
        Assert.AreEqual(
            "RUNNING",
            Scalar(raw, "SELECT status FROM jobs WHERE job_id = 'job_meta';"));
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT provenance_id FROM jobs WHERE job_id = 'job_meta';")));

        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_minimal', '2026-01-01T00:00:00Z');");
        Assert.IsTrue(
            Scalar(raw, "SELECT reported_directory FROM jobs WHERE job_id = 'job_minimal';") is DBNull);

        AssertSqliteFailure(
            () => Execute(
                raw,
                "UPDATE jobs SET provenance_id = 999 WHERE job_id = 'job_meta';"));
    }

    [TestMethod]
    public void TaskRuns_SupportHistoricalSecondaryMappingsWithoutSecondaryAdditiveCost()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_runs");
        InsertTask(raw, "tsk_runs", "prj_runs");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_runs', '2026-01-01T00:00:00Z');");

        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_primary', 'tsk_runs', 'job_runs', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, association_role, status, started_utc) " +
            $"VALUES ('run_secondary', 'tsk_runs', 'job_runs', 'secondary', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, association_role, is_active, retired_utc, status, started_utc) " +
            "VALUES ('run_retired', 'tsk_runs', 'job_runs', 'primary', 0, " +
            $"'2026-01-02T00:00:00Z', 'completed', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
                $"VALUES ('run_conflict', 'tsk_runs', 'job_runs', 'running', '{Now}');"));

        Assert.AreEqual(3L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM task_runs;")));
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(raw, "SELECT is_active FROM task_runs WHERE run_id = 'run_retired';")));
        Assert.AreEqual(
            "2026-01-02T00:00:00Z",
            Scalar(raw, "SELECT retired_utc FROM task_runs WHERE run_id = 'run_retired';"));

        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc, cost_text) " +
            $"VALUES ('use_primary', 'inst', 'src_primary', 'run', 'reported', 'run_primary', 'primary', '{Now}', '0.100');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc, cost_text) " +
            $"VALUES ('use_secondary', 'inst', 'src_secondary', 'run', 'reported', 'run_secondary', 'secondary', '{Now}', '0.200');");

        InsertAppliedLogicalEvent(raw, "job_runs", "0.10");
        Execute(
            raw,
            "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
            $"VALUES ('use_primary', 1, 'Applied', '0.10', 'primary', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
                $"VALUES ('use_secondary', 1, 'Applied', '0.10', 'secondary', '{Now}');"),
            "UNIQUE");

        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM cost_attributions;")));
    }

    [TestMethod]
    public void ProjectPaths_RetainOriginalAndNormalizedKeyWithConfirmedActiveOwnership()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_paths_a");
        InsertProject(raw, "prj_paths_b");
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_paths_a', 'C:\\Work\\Repo', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_paths_b', 'c:\\work\\repo', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_paths_b', 'C:\\Work\\Repo2', 'c:\\work\\repo2', 'root', 'insensitive', '{Now}', '{Now}');"));

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, is_active, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_paths_b', 'C:\\Work\\Repo', NULL, 'root', 'insensitive', 0, '{Now}', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "UPDATE project_paths SET normalized_path = 'c:\\work\\repo', is_active = 1, confirmed_utc = '2026-01-02T00:00:00Z' " +
                "WHERE project_id = 'prj_paths_b';"));

        Assert.AreEqual(
            "C:\\Work\\Repo",
            Scalar(raw, "SELECT original_path FROM project_paths WHERE project_id = 'prj_paths_a';"));
        Assert.AreEqual(
            "c:\\work\\repo",
            Scalar(raw, "SELECT normalized_path FROM project_paths WHERE project_id = 'prj_paths_a';"));
        Assert.AreEqual(
            "insensitive",
            Scalar(raw, "SELECT case_policy FROM project_paths WHERE project_id = 'prj_paths_a';"));
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(raw, "SELECT is_active FROM project_paths WHERE project_id = 'prj_paths_b';")));
    }

    [TestMethod]
    public void ExternalSessionsAndMessages_AreScopedBySourceInstanceAndOwningSession()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertSession(raw, "inst_a", "ses_shared");
        InsertSession(raw, "inst_b", "ses_shared");
        InsertSession(raw, "inst_a", "ses_other");
        InsertMessage(raw, "inst_a", "ses_shared", "msg_shared", 0);
        InsertMessage(raw, "inst_b", "ses_shared", "msg_shared", 0);
        InsertMessage(raw, "inst_a", "ses_other", "msg_shared", 0);

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
                $"VALUES ('inst_a', 'ses_shared', 'msg_shared', 'assistant', 1, '{Now}');"));

        InsertMessage(raw, "inst_a", "ses_shared", "msg_other", 1);
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
                $"VALUES ('inst_a', 'ses_shared', 'msg_duplicate_sequence', 'assistant', 1, '{Now}');"));

        InsertSession(raw, "inst_a", "ses_child", parentSessionId: "ses_shared");
        Execute(
            raw,
            "INSERT INTO opencode_sessions(source_instance_id, session_id, status, started_utc, updated_utc) " +
            $"VALUES ('inst_b', 'ses_child_b', 'active', '{Now}', '{Now}');");
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO opencode_sessions(source_instance_id, session_id, parent_session_id, status, started_utc, updated_utc) " +
                $"VALUES ('inst_a', 'ses_cross_parent', 'ses_child_b', 'active', '{Now}', '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
                $"VALUES ('inst_a', 'missing_session', 'msg_cross', 'assistant', 0, '{Now}');"));

        Assert.AreEqual(
            3L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT COUNT(*) FROM messages WHERE message_id = 'msg_shared';")));
    }

    [TestMethod]
    public void Usage_RejectsWrongSessionMessageAndNonnumericCost()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_usage");
        InsertSession(raw, "inst", "ses_a");
        InsertSession(raw, "inst", "ses_b");
        InsertMessage(raw, "inst", "ses_a", "msg_a", 0);
        InsertMessage(raw, "inst", "ses_b", "msg_b", 0);

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc, cost_text) " +
                $"VALUES ('use_bad_session', 'inst', 'src_bad_session', 'message', 'reported', 'ses_a', 'msg_b', '{Now}', '0.10');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, message_id, observed_utc, cost_text) " +
                $"VALUES ('use_missing_session', 'inst', 'src_missing_session', 'message', 'reported', 'msg_a', '{Now}', '0.10');"));

        var badCosts = new[] { "abc", "1.2.3", ".5", "5.", "1e3", "-1" };
        for (var index = 0; index < badCosts.Length; index++)
        {
            var badCost = badCosts[index];
            AssertSqliteFailure(
                () => Execute(
                    raw,
                    "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc, cost_text) " +
                    $"VALUES ('use_bad_cost_{index}', 'inst', 'src_bad_cost_{index}', 'message', 'reported', 'ses_a', 'msg_a', '{Now}', '{badCost}');"));
        }

        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc, cost_text) " +
            $"VALUES ('use_good', 'inst', 'src_good', 'message', 'reported', 'ses_a', 'msg_a', '{Now}', '0.100');");

        Assert.AreEqual(
            "0.100",
            Scalar(raw, "SELECT cost_text FROM usage_observations WHERE usage_id = 'use_good';"));
    }

    [TestMethod]
    public void Usage_SessionTotalsCannotBeForgeAdditive()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        CollectionAssert.DoesNotContain(
            QueryStrings(raw, "SELECT name FROM pragma_table_info('usage_observations') ORDER BY name;"),
            "forge_additive");
        InsertSession(raw, "inst", "ses_usage");
        InsertMessage(raw, "inst", "ses_usage", "msg_usage", 0);

        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc, cost_text) " +
            $"VALUES ('use_session_ok', 'inst', 'src_session_ok', 'session', 'reported', 'ses_usage', '{Now}', '0.10');");

        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc, cost_text) " +
            $"VALUES ('use_message_ok', 'inst', 'src_message_ok', 'message', 'reported', 'ses_usage', 'msg_usage', '{Now}', '0.10');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc) " +
                $"VALUES ('use_bad_authority', 'inst', 'src_bad_authority', 'session', 'guessed', 'ses_usage', '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc) " +
                $"VALUES ('use_dup_source', 'inst', 'src_session_ok', 'session', 'reported', 'ses_usage', '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, provenance_id, observed_utc) " +
                $"VALUES ('use_bad_provenance', 'inst', 'src_bad_provenance', 'session', 'reported', 'ses_usage', 999, '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, logical_event_id, observed_utc) " +
                $"VALUES ('use_bad_event', 'inst', 'src_bad_event', 'session', 'reported', 'ses_usage', 999, '{Now}');"));

        Assert.AreEqual(
            "0.10",
            Scalar(raw, "SELECT cost_text FROM usage_observations WHERE usage_id = 'use_session_ok';"));
        Assert.AreEqual(
            "0.10",
            Scalar(raw, "SELECT cost_text FROM usage_observations WHERE usage_id = 'use_message_ok';"));
    }

    [TestMethod]
    public void SegmentRunMapping_RejectsContradictoryAssociation()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_seg");
        InsertTask(raw, "tsk_seg", "prj_seg");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_seg', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_seg_a', 'tsk_seg', 'job_seg', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, association_role, status, started_utc) " +
            $"VALUES ('run_seg_b', 'tsk_seg', 'job_seg', 'secondary', 'running', '{Now}');");
        InsertSession(raw, "inst", "ses_seg");
        InsertSegment(raw, "inst", "seg_run_a", "ses_seg", "run_seg_a");
        InsertSegment(raw, "inst", "seg_run_b", "ses_seg", "run_seg_b");

        Execute(
            raw,
            "INSERT INTO run_segments(run_id, source_instance_id, segment_id, position) " +
            "VALUES ('run_seg_a', 'inst', 'seg_run_a', 0);");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO run_segments(run_id, source_instance_id, segment_id, position) " +
                "VALUES ('run_seg_b', 'inst', 'seg_run_a', 1);"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO run_segments(run_id, source_instance_id, segment_id, position) " +
                "VALUES ('run_seg_a', 'inst', 'seg_run_b', 1);"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "UPDATE execution_segments SET run_id = 'run_seg_b' " +
                "WHERE source_instance_id = 'inst' AND segment_id = 'seg_run_a';"));

        Assert.AreEqual(1L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM run_segments;")));
    }

    [TestMethod]
    public void TransactionalConstraintFailure_RollsBackWholeAssociation()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_tx_a");
        InsertProject(raw, "prj_tx_b");
        Execute(raw, "BEGIN IMMEDIATE;");
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_tx_a', 'C:\\Tx', 'c:\\tx', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_tx_b', 'C:\\Tx', 'c:\\tx', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"));

        Execute(raw, "ROLLBACK;");
        Assert.AreEqual(0L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM project_paths;")));
    }

    [TestMethod]
    public void ActivePrimaryAttribution_IsUniqueAndDecimalTextIsExact()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_a");
        InsertTask(raw, "tsk_a", "prj_a");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_a', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_a', 'tsk_a', 'job_a', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc, cost_text) " +
            $"VALUES ('use_a', 'inst', 'src_use_a', 'run', 'reported', 'run_a', 'primary', '{Now}', '0.100');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc, cost_text) " +
            $"VALUES ('use_b', 'inst', 'src_use_b', 'run', 'reported', 'run_a', 'primary', '{Now}', '0.100');");
        InsertAppliedLogicalEvent(raw, "job_a", "0.100");

        Execute(
            raw,
            "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
            $"VALUES ('use_a', 1, 'Applied', '0.100', 'primary', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
                $"VALUES ('use_a', 1, 'Applied', '0.100', 'primary', '{Now}');"),
            "UNIQUE");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
                $"VALUES ('use_b', 1, 'Applied', '0.100', 'primary', '{Now}');"),
            "UNIQUE");

        Execute(raw, "UPDATE cost_attributions SET is_active = 0 WHERE usage_id = 'use_a';");
        Execute(
            raw,
            "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
            $"VALUES ('use_a', 1, 'Applied', '0.100', 'primary', '2026-01-01T00:00:01Z');");

        Assert.AreEqual(
            "0.100",
            Scalar(raw, "SELECT logical_event_cost_text FROM cost_attributions WHERE is_active = 0;"));
        Assert.AreEqual(
            "0.100",
            Scalar(raw, "SELECT logical_event_cost_text FROM cost_attributions WHERE is_active = 1;"));
    }

    [TestMethod]
    public void Ingest_InjectedCommitFailure_RollsBackV3Projection()
    {
        var logPath = _workspace.LogPath("job_v3_rollback");
        _workspace.WriteLog(
            "job_v3_rollback",
            TestWorkspace.StepStart(1_800_000_000_000, part: "v3-rb-1", message: "v3-rb-1"));

        using (var first = CreateLedger())
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                first.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        _workspace.AppendLog(
            "job_v3_rollback",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.5m,
                part: "v3-rb-2",
                message: "v3-rb-2"));

        using var ledger = CreateLedger(new EventLedgerOptions
        {
            BeforeCommitFaultInjector = () =>
                throw new LedgerException(
                    LedgerStorageFailure.Unavailable,
                    "Injected V3 commit failure.")
        });

        var before = ledger.GetJobAggregate(JobId);
        var generation = ledger.ListGenerations(JobId).Single();
        var recordsBefore = ledger.ListRecords(generation.GenerationId).Count;

        Assert.ThrowsException<LedgerException>(
            () => ledger.Ingest(JobId, logPath, _workspace.RunsRoot));

        var after = ledger.GetJobAggregate(JobId);
        Assert.AreEqual(before.LogicalEventCount, after.LogicalEventCount);
        Assert.AreEqual(before.CostTotal, after.CostTotal);
        Assert.AreEqual(before.Version, after.Version);
        Assert.AreEqual(recordsBefore, ledger.ListRecords(generation.GenerationId).Count);
        Assert.AreEqual(
            generation.CommittedOffset,
            ledger.ListGenerations(JobId).Single().CommittedOffset);
    }

    [TestMethod]
    public void CorruptDatabase_FailsClosedAndIsNotDeleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var bytes = Encoding.UTF8.GetBytes("this is not a sqlite v3 database");
        File.WriteAllBytes(_databasePath, bytes);

        AssertLedgerFailure(
            LedgerStorageFailure.Corrupt,
            () => CreateLedger());

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(_databasePath));
    }

    [TestMethod]
    public void ConcurrentIngest_TwoInstancesCommitIndependentJobs()
    {
        var firstLog = _workspace.LogPath("job_v3_concurrent_a");
        var secondLog = _workspace.LogPath("job_v3_concurrent_b");
        _workspace.WriteLog(
            "job_v3_concurrent_a",
            TestWorkspace.StepFinish(
                1_800_000_001_000,
                0.10m,
                session: "ses_v3_a",
                part: "v3-a",
                message: "v3-a"));
        _workspace.WriteLog(
            "job_v3_concurrent_b",
            TestWorkspace.StepFinish(
                1_800_000_002_000,
                0.20m,
                session: "ses_v3_b",
                part: "v3-b",
                message: "v3-b"));

        using var first = CreateLedger(new EventLedgerOptions { BusyTimeoutMilliseconds = 10_000 });
        using var second = CreateLedger(new EventLedgerOptions { BusyTimeoutMilliseconds = 10_000 });

        var results = Task.WhenAll(
            Task.Run(() => first.Ingest("job_v3_concurrent_a", firstLog, _workspace.RunsRoot)),
            Task.Run(() => second.Ingest("job_v3_concurrent_b", secondLog, _workspace.RunsRoot)))
            .GetAwaiter()
            .GetResult();

        Assert.AreEqual(2, results.Length);
        Assert.IsTrue(results.All(result => result.Status == LedgerIngestStatus.Committed));
        Assert.AreEqual(0.10m, first.GetJobAggregate("job_v3_concurrent_a").CostTotal);
        Assert.AreEqual(0.20m, second.GetJobAggregate("job_v3_concurrent_b").CostTotal);
        Assert.AreEqual(1, first.GetJobAggregate("job_v3_concurrent_a").LogicalEventCount);
        Assert.AreEqual(1, second.GetJobAggregate("job_v3_concurrent_b").LogicalEventCount);
    }

    [TestMethod]
    public void DecimalCost_IsPreservedExactlyAcrossV3Storage()
    {
        var logPath = _workspace.LogPath("job_v3_decimal");
        File.WriteAllText(
            logPath,
            """
            {"type":"step_finish","timestamp":1800000001000,"sessionID":"ses_v3_decimal","part":{"id":"v3-decimal","messageID":"v3-decimal","type":"step-finish","reason":"stop","cost":0.10}}
            """ + "\n",
            new UTF8Encoding(false));

        using (var ledger = CreateLedger())
        {
            var result = ledger.Ingest(JobId, logPath, _workspace.RunsRoot);
            Assert.AreEqual(LedgerIngestStatus.Committed, result.Status);
            Assert.AreEqual(0.10m, ledger.GetJobAggregate(JobId).CostTotal);
        }

        using var raw = OpenRawConnection(_databasePath);
        Assert.AreEqual(
            "0.10",
            Scalar(raw, "SELECT cost_text FROM logical_events WHERE decision = 'Applied';"));
        Assert.AreEqual(
            "0.10",
            Scalar(raw, "SELECT cost_text FROM job_logical_keys;"));
        Assert.AreEqual(
            "0.10",
            Convert.ToString(
                Scalar(raw, "SELECT cost_total FROM job_aggregates;"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void V1AndV2DatabasesAndSidecars_ArePreservedWhenV3IsCreated()
    {
        var logPath = _workspace.LogPath("job_v3_preserve");
        _workspace.WriteLog("job_v3_preserve", TestWorkspace.StepStart(1_800_000_000_000));

        using (var v1 = new EventLedger(_v1Path, _workspace.Root))
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                v1.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        using (var v2 = EventLedger.OpenProjection(_v2Path, _workspace.Root))
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                v2.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        var v1Before = SnapshotFiles(_v1Path);
        var v2Before = SnapshotFiles(_v2Path);
        Assert.IsTrue(v1Before.Keys.Any(path => path.EndsWith("events.db", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(v2Before.Keys.Any(path => path.EndsWith("events-v2.db", StringComparison.OrdinalIgnoreCase)));

        using (var v3 = CreateLedger())
        {
            Assert.AreEqual(
                LedgerIngestStatus.Committed,
                v3.Ingest(JobId, logPath, _workspace.RunsRoot).Status);
        }

        AssertPreserved(v1Before, _v1Path);
        AssertPreserved(v2Before, _v2Path);

        using var rawV1 = OpenRawConnection(_v1Path, readOnly: true);
        using var rawV2 = OpenRawConnection(_v2Path, readOnly: true);
        Assert.AreEqual(1L, Convert.ToInt64(Scalar(rawV1, "PRAGMA user_version;")));
        Assert.AreEqual(2L, Convert.ToInt64(Scalar(rawV2, "PRAGMA user_version;")));
    }

    [TestMethod]
    public void AdditiveCost_RequiresForgeAppliedStepAndPrimaryOwnership()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_additive");
        InsertTask(raw, "tsk_additive", "prj_additive");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_additive', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_add_primary', 'tsk_additive', 'job_additive', 'running', '{Now}');");
        InsertSession(raw, "inst", "ses_additive");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc, cost_text) " +
            $"VALUES ('use_nonadditive', 'inst', 'src_nonadditive', 'session', 'reported', 'ses_additive', '{Now}', '0.10');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, observed_utc, forge_additive) " +
                $"VALUES ('use_old_additive', 'inst', 'src_old_additive', 'run', 'reported', 'run_add_primary', '{Now}', 1);"),
            "forge_additive");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO cost_attributions(usage_id, is_additive, cost_text, allocated_utc) " +
                $"VALUES ('use_nonadditive', 1, '0.10', '{Now}');"),
            "no column named");

        InsertAppliedLogicalEvent(raw, "job_additive", "0.10");
        Execute(
            raw,
            "INSERT INTO cost_attributions(usage_id, logical_event_id, logical_event_decision, logical_event_cost_text, attribution_role, allocated_utc) " +
            $"VALUES ('use_nonadditive', 1, 'Applied', '0.10', 'primary', '{Now}');");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM cost_attributions;")));
    }

    [TestMethod]
    public void Messages_RequireDistinctTypesAndScopedNullableParent()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertSession(raw, "inst", "ses_messages");
        InsertSession(raw, "inst", "ses_messages_other");

        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('inst', 'ses_messages', 'msg_user', 'user', 0, '{Now}');");
        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('inst', 'ses_messages', 'msg_assistant', 'assistant', 1, '{Now}');");
        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('inst', 'ses_messages', 'msg_idle', 'idle', 2, '{Now}');");
        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('inst', 'ses_messages', 'msg_switched', 'agent-switched', 3, '{Now}');");
        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, parent_message_id, created_utc) " +
            $"VALUES ('inst', 'ses_messages', 'msg_parented', 'assistant', 4, 'msg_user', '{Now}');");
        Execute(
            raw,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('inst', 'ses_messages_other', 'msg_other', 'assistant', 0, '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, parent_message_id, created_utc) " +
                $"VALUES ('inst', 'ses_messages', 'msg_cross_parent', 'assistant', 5, 'msg_other', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, parent_message_id, created_utc) " +
                $"VALUES ('inst', 'ses_messages', 'msg_self_parent', 'assistant', 6, 'msg_self_parent', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
                $"VALUES ('inst', 'ses_messages', 'msg_system', 'system', 7, '{Now}');"));

        Assert.AreEqual(
            6L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM messages;")));
    }

    [TestMethod]
    public void UsageScopes_RequireExactCompatibleTarget()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_scope");
        InsertTask(raw, "tsk_scope", "prj_scope");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_scope', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_scope', 'tsk_scope', 'job_scope', 'running', '{Now}');");
        InsertSession(raw, "inst", "ses_scope");
        InsertMessage(raw, "inst", "ses_scope", "msg_scope", 0);
        Execute(
            raw,
            "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_scope', 'run_scope', 0, 'running', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc) " +
                $"VALUES ('use_msg_wrong', 'inst', 'src_msg_wrong', 'message', 'reported', 'run_scope', 'primary', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc) " +
                $"VALUES ('use_session_with_message', 'inst', 'src_session_with_message', 'session', 'reported', 'ses_scope', 'msg_scope', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc) " +
                $"VALUES ('use_run_with_message', 'inst', 'src_run_with_message', 'run', 'reported', 'ses_scope', 'msg_scope', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc) " +
                $"VALUES ('use_invocation_with_run', 'inst', 'src_invocation_with_run', 'invocation', 'reported', 'run_scope', 'primary', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc) " +
                $"VALUES ('use_message_no_target', 'inst', 'src_message_no_target', 'message', 'reported', 'ses_scope', '{Now}');"));

        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, message_id, observed_utc) " +
            $"VALUES ('use_message_ok', 'inst', 'src_message_ok', 'message', 'reported', 'ses_scope', 'msg_scope', '{Now}');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc) " +
            $"VALUES ('use_session_ok', 'inst', 'src_session_ok_scope', 'session', 'reported', 'ses_scope', '{Now}');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, run_id, run_association_role, observed_utc) " +
            $"VALUES ('use_run_ok', 'inst', 'src_run_ok_scope', 'run', 'reported', 'run_scope', 'primary', '{Now}');");
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, invocation_id, invocation_run_id, invocation_run_association_role, observed_utc) " +
            $"VALUES ('use_invocation_ok', 'inst', 'src_invocation_ok_scope', 'invocation', 'reported', 'inv_scope', 'run_scope', 'primary', '{Now}');");

        Assert.AreEqual(
            4L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM usage_observations;")));
    }

    [TestMethod]
    public void ProjectPaths_RequireDeterministicCanonicalKeysForConfirmedActiveOwnership()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_case_a");
        InsertProject(raw, "prj_case_b");
        InsertProject(raw, "prj_case_c");
        InsertProject(raw, "prj_case_d");
        InsertProject(raw, "prj_case_e");
        InsertProject(raw, "prj_case_f");

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_case_a', 'C:\\Work\\Repo', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");

        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_case_b', 'C:\\Work\\REPO', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_case_b', 'C:\\Work\\REPO', 'C:\\Work\\REPO', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"));

        AssertSqliteFailure(
            () => Execute(
                raw,
                "UPDATE project_paths SET original_path = 'C:\\Work\\Repo2' WHERE project_id = 'prj_case_a';"));
        AssertSqliteFailure(
            () => Execute(
                raw,
                "UPDATE project_paths SET normalized_path = 'C:\\Work\\REPO' WHERE project_id = 'prj_case_a';"));

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, is_active, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_case_c', 'C:\\Case\\Sensitive', NULL, 'root', 'sensitive', 0, '{Now}', '{Now}');");
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_case_d', 'C:\\One\\Repo', 'c:\\one\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_case_e', 'C:\\Two\\Repo', 'c:\\two\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, is_active, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_case_f', 'C:\\Case\\sensitive', NULL, 'root', 'sensitive', 0, '{Now}', '{Now}');");

        Assert.AreEqual(
            "C:\\Work\\Repo",
            Scalar(raw, "SELECT original_path FROM project_paths WHERE project_id = 'prj_case_a';"));
        Assert.AreEqual(
            "C:\\Case\\Sensitive",
            Scalar(raw, "SELECT original_path FROM project_paths WHERE project_id = 'prj_case_c';"));
        Assert.AreEqual(
            "C:\\Case\\sensitive",
            Scalar(raw, "SELECT original_path FROM project_paths WHERE project_id = 'prj_case_f';"));
    }

    [TestMethod]
    public void AdditiveColumns_AreRemovedAndUsageRemainsNonadditive()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        var usageColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('usage_observations') ORDER BY name;");
        var attributionColumns = QueryStrings(raw, "SELECT name FROM pragma_table_info('cost_attributions') ORDER BY name;");
        CollectionAssert.DoesNotContain(usageColumns, "forge_additive");
        CollectionAssert.DoesNotContain(attributionColumns, "is_additive");
        CollectionAssert.DoesNotContain(attributionColumns, "cost_text");
        CollectionAssert.DoesNotContain(attributionColumns, "cost_currency");
        CollectionAssert.DoesNotContain(attributionColumns, "usage_forge_additive");
        CollectionAssert.Contains(attributionColumns, "logical_event_id");

        var indexes = QueryStrings(
            raw,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT GLOB 'sqlite_autoindex_*' ORDER BY name;");
        CollectionAssert.Contains(indexes, "ux_cost_attributions_active_event");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT COUNT(*) FROM pragma_table_info('job_aggregates') WHERE name = 'cost_total';")));
    }

    [TestMethod]
    public void NewIdentities_AreNotNullTypedBoundedAndNulFree()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        var primaryKeyTables = new[]
        {
            "projects", "tasks", "task_runs", "delegation_invocations",
            "opencode_sessions", "messages", "execution_segments", "content_items",
            "results", "execution_evidence", "validations", "enrichment_imports",
            "usage_observations", "monitoring_starts", "jobs"
        };
        foreach (var table in primaryKeyTables)
        {
            var pkColumns = QueryStrings(
                raw,
                $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk;");
            Assert.IsTrue(pkColumns.Count > 0, table);
            foreach (var column in pkColumns)
            {
                var notNull = Convert.ToInt64(
                    Scalar(
                        raw,
                        "SELECT \"notnull\" FROM pragma_table_info('" + table + "') " +
                        $"WHERE name = '{column}';"));
                Assert.AreEqual(1L, notNull, table + "." + column);
            }
        }

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO projects(project_id, display_name, created_utc, updated_utc) " +
                $"VALUES (NULL, 'p', '{Now}', '{Now}');"),
            "NOT NULL");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO projects(project_id, display_name, created_utc, updated_utc) " +
                $"VALUES (CAST('a' AS BLOB), 'p', '{Now}', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO projects(project_id, display_name, created_utc, updated_utc) " +
                $"VALUES ('a' || char(0) || 'b', 'p', '{Now}', '{Now}');"),
            "CHECK");
    }

    [TestMethod]
    public void PathOwnership_RejectsUnicodeOwnersAndAllowsSameProjectAliases()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_unicode_a");
        InsertProject(raw, "prj_unicode_b");
        InsertProject(raw, "prj_alias");
        InsertProject(raw, "prj_sensitive");
        InsertProject(raw, "prj_sep");
        InsertProject(raw, "prj_dot");
        InsertProject(raw, "prj_trail");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_unicode_a', 'C:\\Work\\ÄRepo', 'c:\\work\\Ärepo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_unicode_b', 'C:\\Work\\äRepo', 'c:\\work\\ärepo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"),
            "CHECK");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_sensitive', 'C:\\Work\\Repo', 'C:\\Work\\Repo', 'root', 'sensitive', '{Now}', '{Now}', '{Now}');"),
            "CHECK");

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_alias', 'C:\\Work\\Repo', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");
        var ownerId = Convert.ToInt64(Scalar(
            raw,
            "SELECT project_path_id FROM project_paths WHERE project_id = 'prj_alias';"));
        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, alias_of_project_path_id, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_alias', 'C:\\WORK\\REPO', 'c:\\work\\repo', 'root', 'insensitive', '{Now}', {ownerId}, '{Now}', '{Now}');");

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_sep', 'C:/Work/Repo3', 'c:\\work\\repo3', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_dot', 'C:\\Work\\.\\Repo', 'c:\\work\\.\\repo', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, confirmed_utc, first_seen_utc, last_seen_utc) " +
                $"VALUES ('prj_trail', 'C:\\Work\\Repo4\\', 'c:\\work\\repo4\\', 'root', 'insensitive', '{Now}', '{Now}', '{Now}');"),
            "CHECK");

        Execute(
            raw,
            "INSERT INTO project_paths(project_id, original_path, normalized_path, path_kind, case_policy, is_active, confirmed_utc, first_seen_utc, last_seen_utc) " +
            $"VALUES ('prj_unicode_a', '\\\\server\\share', NULL, 'root', 'sensitive', 0, NULL, '{Now}', '{Now}');");

        Assert.AreEqual(
            2L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT COUNT(*) FROM project_paths WHERE project_id = 'prj_alias';")));
    }

    [TestMethod]
    public void Content_RequiresSameSessionAndConfirmedSegmentMembership()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertSession(raw, "inst", "ses_content_a");
        InsertSession(raw, "inst", "ses_content_b");
        InsertMessage(raw, "inst", "ses_content_a", "msg_content_a", 0);
        InsertMessage(raw, "inst", "ses_content_b", "msg_content_b", 0);
        InsertSegment(raw, "inst", "seg_content_a", "ses_content_a", runId: null);
        InsertSegment(raw, "inst", "seg_content_b", "ses_content_b", runId: null);
        Execute(
            raw,
            "INSERT INTO segment_messages(source_instance_id, segment_id, message_id, session_id, position) " +
            "VALUES ('inst', 'seg_content_a', 'msg_content_a', 'ses_content_a', 0);");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO content_items(content_id, source_instance_id, message_id, session_id, segment_id, content_kind, created_utc) " +
                $"VALUES ('content_cross', 'inst', 'msg_content_a', 'ses_content_a', 'seg_content_b', 'text', '{Now}');"),
            "FOREIGN KEY");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO content_items(content_id, source_instance_id, session_id, segment_id, content_kind, created_utc) " +
                $"VALUES ('content_missing_session', 'inst', 'ses_missing', 'seg_content_a', 'text', '{Now}');"),
            "FOREIGN KEY");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO content_items(content_id, source_instance_id, message_id, session_id, segment_id, content_kind, created_utc) " +
                $"VALUES ('content_not_member', 'inst', 'msg_content_b', 'ses_content_b', 'seg_content_a', 'text', '{Now}');"),
            "FOREIGN KEY");

        Execute(
            raw,
            "INSERT INTO content_items(content_id, source_instance_id, message_id, session_id, segment_id, content_kind, created_utc) " +
            $"VALUES ('content_ok', 'inst', 'msg_content_a', 'ses_content_a', 'seg_content_a', 'text', '{Now}');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "DELETE FROM execution_segments WHERE source_instance_id = 'inst' AND segment_id = 'seg_content_a';"),
            "FOREIGN KEY");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "DELETE FROM messages WHERE source_instance_id = 'inst' AND session_id = 'ses_content_a' AND message_id = 'msg_content_a';"),
            "FOREIGN KEY");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM content_items;")));
    }

    [TestMethod]
    public void NullableUniqueness_IsClosedForProvenanceAndObservationContext()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_null_unique");
        InsertTask(raw, "tsk_null_unique", "prj_null_unique");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_null_unique', '2026-01-01T00:00:00Z');");
        InsertAppliedLogicalEvent(raw, "job_null_unique", "0.10");

        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, project_id, source_kind, observed_utc) " +
            $"VALUES ('project', 'prj_null_unique', 'prj_null_unique', 'forge', '{Now}');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO provenance(entity_type, entity_id, project_id, source_kind, observed_utc) " +
                $"VALUES ('project', 'prj_null_unique', 'prj_null_unique', 'forge', '{Now}');"),
            "UNIQUE");

        Execute(
            raw,
            "INSERT INTO event_observation_context(logical_event_id, observed_utc, context_kind) " +
            $"VALUES (1, '{Now}', 'bootstrap');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO event_observation_context(logical_event_id, observed_utc, context_kind) " +
                $"VALUES (1, '{Now}', 'bootstrap');"),
            "UNIQUE");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO event_observation_context(logical_event_id, observed_utc, context_kind) " +
                $"VALUES (1, '{Now}', 'live');"),
            "CHECK");
    }

    [TestMethod]
    public void ScalarIntegrity_RejectsFractionalTokensBlobTextNulAndDecimalOverflow()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_scalar");
        InsertSession(raw, "inst", "ses_scalar");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, input_tokens, observed_utc) " +
                $"VALUES ('use_fractional_bad', 'inst', 'src_fractional_bad', 'session', 'reported', 'ses_scalar', 1.5, '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, cost_text, observed_utc) " +
                $"VALUES ('use_decimal_overflow', 'inst', 'src_decimal_overflow', 'session', 'reported', 'ses_scalar', '123456789012345678901234567890', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, cost_currency, observed_utc) " +
                $"VALUES ('use_currency_bad', 'inst', 'src_currency_bad', 'session', 'reported', 'ses_scalar', 'usd', '{Now}');"),
            "CHECK");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(project_id, display_name, updated_utc) " +
                $"VALUES ('prj_scalar', CAST('blob' AS BLOB), '{Now}');"),
            "CHECK");
        var nulSuffix = new string('b', 1000);
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(project_id, display_name, updated_utc) " +
                $"VALUES ('prj_scalar', 'a' || char(0) || '{nulSuffix}', '{Now}');"),
            "CHECK");
    }

    [TestMethod]
    public void DeliveryCuts_RequireMatchingJobGenerationAndCommittedOffset()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_cut_a', '2026-01-01T00:00:00Z');");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_cut_b', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO source_generations(generation_id, job_id, source_path, volume_serial, file_index, creation_utc_ticks, first_seen_utc, last_seen_utc) " +
            $"VALUES (1, 'job_cut_a', 'a.log', 1, '1', 1, '{Now}', '{Now}');");
        Execute(
            raw,
            "INSERT INTO source_generations(generation_id, job_id, source_path, volume_serial, file_index, creation_utc_ticks, first_seen_utc, last_seen_utc) " +
            $"VALUES (2, 'job_cut_b', 'b.log', 1, '2', 2, '{Now}', '{Now}');");
        Execute(raw, "INSERT INTO source_cursors(generation_id, committed_offset, version) VALUES (1, 10, 0);");
        Execute(raw, "INSERT INTO source_cursors(generation_id, committed_offset, version) VALUES (2, 20, 0);");
        Execute(
            raw,
            "INSERT INTO monitoring_starts(start_id, started_utc, status) " +
            $"VALUES ('start_cut', '{Now}', 'running');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
                $"VALUES ('start_cut', 'job_cut_b', 1, 10, 'log', 5, '{Now}');"),
            "FOREIGN KEY");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
                $"VALUES ('start_cut', 'job_cut_a', 1, 10, 'log', 99, '{Now}');"),
            "CHECK");

        Execute(
            raw,
            "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
            $"VALUES ('start_cut', 'job_cut_a', 1, 10, 'log', 5, '{Now}');");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM source_delivery_cuts;")));
    }

    [TestMethod]
    public void AdditionalOwnership_ResultValidationEnrichmentExternalAndParentCycles()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_extra");
        InsertTask(raw, "tsk_extra", "prj_extra");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_extra', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_extra_a', 'tsk_extra', 'job_extra', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, status, started_utc) " +
            $"VALUES ('run_extra_b', 'tsk_extra', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_extra_b', 'run_extra_b', 0, 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_extra_a', 'run_extra_a', 'passed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO enrichment_imports(import_id, source_kind, content_sha256, imported_utc, status) " +
            $"VALUES ('import_failed', 'manual', '{new string('a', 64)}', '{Now}', 'failed');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO results(result_id, run_id, invocation_id, invocation_run_id, status, created_utc) " +
                $"VALUES ('result_bad_owner', 'run_extra_a', 'inv_extra_b', 'run_extra_b', 'passed', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO validations(validation_id, result_id, result_run_id, run_id, validation_kind, status, created_utc) " +
                $"VALUES ('validation_bad_owner', 'result_extra_a', 'run_extra_a', 'run_extra_b', 'test', 'passed', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO run_enrichment(run_id, import_id, import_status, enrichment_kind, applied_utc) " +
                $"VALUES ('run_extra_a', 'import_failed', 'failed', 'metadata', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO external_references(run_id, reference_kind, external_id, source_system, created_utc) " +
                $"VALUES ('run_extra_a', 'issue', 'external-1', NULL, '{Now}');"),
            "NOT NULL");
        Execute(
            raw,
            "INSERT INTO external_references(run_id, reference_kind, external_id, source_system, created_utc) " +
            $"VALUES ('run_extra_a', 'issue', 'external-1', 'alpha', '{Now}');");
        Execute(
            raw,
            "INSERT INTO external_references(run_id, reference_kind, external_id, source_system, created_utc) " +
            $"VALUES ('run_extra_a', 'issue', 'external-1', 'beta', '{Now}');");
        Execute(
            raw,
            "INSERT INTO external_references(run_id, reference_kind, external_id, source_system, created_utc) " +
            $"VALUES ('run_extra_b', 'issue', 'external-1', 'alpha', '{Now}');");

        InsertSession(raw, "inst", "ses_cycle");
        InsertMessage(raw, "inst", "ses_cycle", "msg_cycle_a", 0);
        InsertMessage(raw, "inst", "ses_cycle", "msg_cycle_b", 1);
        Execute(
            raw,
            "UPDATE messages SET parent_message_id = 'msg_cycle_b' " +
            "WHERE source_instance_id = 'inst' AND session_id = 'ses_cycle' AND message_id = 'msg_cycle_a';");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE messages SET parent_message_id = 'msg_cycle_a' " +
                "WHERE source_instance_id = 'inst' AND session_id = 'ses_cycle' AND message_id = 'msg_cycle_b';"),
            "cycle");

        InsertSession(raw, "inst", "ses_cycle_a");
        InsertSession(raw, "inst", "ses_cycle_b");
        Execute(
            raw,
            "UPDATE opencode_sessions SET parent_session_id = 'ses_cycle_b' " +
            "WHERE source_instance_id = 'inst' AND session_id = 'ses_cycle_a';");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE opencode_sessions SET parent_session_id = 'ses_cycle_a' " +
                "WHERE source_instance_id = 'inst' AND session_id = 'ses_cycle_b';"),
            "cycle");
    }

    [TestMethod]
    public void PrimaryOwnership_RequiresProjectConsistencyForActivePrimary()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_owner_a");
        InsertProject(raw, "prj_owner_b");
        InsertTask(raw, "tsk_owner_a", "prj_owner_a");
        InsertTask(raw, "tsk_owner_b", "prj_owner_b");
        Execute(raw, "INSERT INTO jobs(job_id, project_id, created_utc) VALUES ('job_owner_a', 'prj_owner_a', '2026-01-01T00:00:00Z');");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_owner_unknown', '2026-01-01T00:00:00Z');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
                $"VALUES ('run_owner_conflict', 'tsk_owner_b', 'job_owner_a', 'running', '{Now}');"),
            "project");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_owner_a', 'tsk_owner_a', 'job_owner_a', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, association_role, status, started_utc) " +
            $"VALUES ('run_owner_secondary', 'tsk_owner_b', 'job_owner_a', 'secondary', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, association_role, is_active, retired_utc, status, started_utc) " +
            "VALUES ('run_owner_retired', 'tsk_owner_b', 'job_owner_a', 'primary', 0, " +
            $"'2026-01-02T00:00:00Z', 'completed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_owner_unknown', 'tsk_owner_b', 'job_owner_unknown', 'running', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE jobs SET project_id = 'prj_owner_b' WHERE job_id = 'job_owner_a';"),
            "project");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE tasks SET project_id = 'prj_owner_b' WHERE task_id = 'tsk_owner_a';"),
            "project");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE task_runs SET job_id = 'job_owner_a' WHERE run_id = 'run_owner_unknown';"),
            "project");

        Execute(raw, "BEGIN IMMEDIATE;");
        Execute(
            raw,
            "UPDATE task_runs SET is_active = 0, retired_utc = '2026-01-03T00:00:00Z' WHERE run_id = 'run_owner_a';");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_owner_reassigned', 'tsk_owner_a', 'job_owner_a', 'running', '{Now}');");
        Execute(raw, "COMMIT;");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT COUNT(*) FROM task_runs WHERE job_id = 'job_owner_a' AND association_role = 'primary' AND is_active = 1;")));
    }

    [DataTestMethod]
    [DataRow("jobs", "INSERT OR REPLACE")]
    [DataRow("jobs", "REPLACE")]
    [DataRow("jobs", "UPDATE")]
    [DataRow("jobs", "UPSERT")]
    [DataRow("jobs", "UPDATE OR REPLACE")]
    [DataRow("tasks", "INSERT OR REPLACE")]
    [DataRow("tasks", "REPLACE")]
    [DataRow("tasks", "UPDATE")]
    [DataRow("tasks", "UPSERT")]
    [DataRow("tasks", "UPDATE OR REPLACE")]
    public void PrimaryOwnership_ParentMutationsPreserveOwnershipAndDependents(string table, string mutation)
    {
        using (var ledger = CreateLedger()) { }
        using (var raw = OpenRawConnection(_databasePath))
        {
            Execute(raw, "PRAGMA recursive_triggers = OFF;");
            InsertProject(raw, "owner_a");
            InsertProject(raw, "owner_b");
            InsertTask(raw, "owner_task", "owner_a");
            InsertTask(raw, "spare_task", "owner_b");
            Execute(raw, $"INSERT INTO jobs(job_id, project_id, created_utc) VALUES ('owner_job', 'owner_a', '{Now}'), ('spare_job', 'owner_b', '{Now}');");
            Execute(raw, $"INSERT INTO task_runs(run_id,task_id,job_id,status,started_utc) VALUES ('owner_run','owner_task','owner_job','running','{Now}');");
            Execute(raw, $"INSERT INTO source_generations(generation_id,job_id,source_path,volume_serial,file_index,creation_utc_ticks,first_seen_utc,last_seen_utc) VALUES (1,'owner_job','synthetic.log',1,'1',1,'{Now}','{Now}');");
            Execute(raw, "INSERT INTO source_cursors(generation_id,committed_offset,version) VALUES (1,10,1);");
            var idColumn = table == "jobs" ? "job_id" : "task_id";
            var id = table == "jobs" ? "owner_job" : "owner_task";
            var spare = table == "jobs" ? "spare_job" : "spare_task";
            string Insert(string verb, string project) => table == "jobs"
                ? $"{verb} INTO jobs(job_id,project_id,created_utc) VALUES ('{id}',{project},'{Now}')"
                : $"{verb} INTO tasks(task_id,project_id,status,created_utc,updated_utc) VALUES ('{id}',{project},'active','{Now}','{Now}')";
            var invalid = mutation switch
            {
                "UPDATE" => $"UPDATE {table} SET project_id='owner_b' WHERE {idColumn}='{id}';",
                "UPDATE OR REPLACE" => $"UPDATE OR REPLACE {table} SET {idColumn}='{id}' WHERE {idColumn}='{spare}';",
                "UPSERT" => Insert("INSERT", "'owner_a'") + $" ON CONFLICT({idColumn}) DO UPDATE SET project_id='owner_b';",
                _ => Insert(mutation, "'owner_b'") + ";"
            };
            Execute(raw, "BEGIN IMMEDIATE;");
            Execute(raw, "UPDATE task_runs SET status='failed' WHERE run_id='owner_run';");
            AssertSqliteFailureContaining(() => Execute(raw, invalid), "primary task/job project conflict");
            Assert.AreEqual("owner_a", Scalar(raw, $"SELECT project_id FROM {table} WHERE {idColumn}='{id}';"));
            Execute(raw, "ROLLBACK;");
            Assert.AreEqual("running", Scalar(raw, "SELECT status FROM task_runs WHERE run_id='owner_run';"));
            Assert.AreEqual("owner_job", Scalar(raw, "SELECT job_id FROM source_generations WHERE generation_id=1;"));
            Assert.AreEqual(10L, Convert.ToInt64(Scalar(raw, "SELECT committed_offset FROM source_cursors WHERE generation_id=1;")));
            Execute(raw, Insert("INSERT OR REPLACE", "'owner_a'") + ";");
            Execute(raw, Insert("REPLACE", "'owner_a'") + ";");
            Execute(raw, $"UPDATE {table} SET project_id=project_id WHERE {idColumn}='{id}';");
            // Discarded conflicting insert values must not reject a harmless conflict action.
            Execute(raw, Insert("INSERT", "'owner_b'") + $" ON CONFLICT({idColumn}) DO NOTHING;");
            Execute(raw, Insert("INSERT", "'owner_b'") + $" ON CONFLICT({idColumn}) DO UPDATE SET project_id='owner_a';");
            Execute(raw, "UPDATE jobs SET project_id=NULL WHERE job_id='owner_job';");
            Execute(raw, Insert("INSERT OR REPLACE", table == "jobs" ? "NULL" : "'owner_b'") + ";");
            Execute(raw, "UPDATE task_runs SET association_role='secondary' WHERE run_id='owner_run';");
            Execute(raw, "UPDATE jobs SET project_id='owner_a' WHERE job_id='owner_job';");
            Execute(raw, Insert("REPLACE", "'owner_b'") + ";");
            Execute(raw, "UPDATE task_runs SET association_role='primary',is_active=0 WHERE run_id='owner_run';");
            Execute(raw, Insert("REPLACE", "'owner_a'") + ";");
            Assert.AreEqual(1L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM task_runs;")));
            Assert.AreEqual(1L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM source_generations;")));
            Assert.AreEqual(10L, Convert.ToInt64(Scalar(raw, "SELECT committed_offset FROM source_cursors WHERE generation_id=1;")));
            Assert.AreEqual(0, QueryStrings(raw, "PRAGMA foreign_key_check;").Count);
        }
        using var reopened = CreateLedger();
        Assert.IsNotNull(reopened.DatabasePath);
    }

    [TestMethod]
    public void DisplayOverrides_RequireTypedScopedTargets()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_override");
        InsertTask(raw, "tsk_override", "prj_override");
        InsertSession(raw, "inst_one", "ses_override");
        InsertSession(raw, "inst_two", "ses_override");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(task_id, display_name, updated_utc) " +
                $"VALUES ('tsk_missing', 'x', '{Now}');"),
            "FOREIGN KEY");
        Execute(
            raw,
            "INSERT INTO display_overrides(project_id, display_name, updated_utc) " +
            $"VALUES ('prj_override', 'Project Label', '{Now}');");
        Execute(
            raw,
            "INSERT INTO display_overrides(task_id, display_name, updated_utc) " +
            $"VALUES ('tsk_override', 'Task Label', '{Now}');");
        Execute(
            raw,
            "INSERT INTO display_overrides(source_instance_id, session_id, display_name, updated_utc) " +
            $"VALUES ('inst_one', 'ses_override', 'One', '{Now}');");
        Execute(
            raw,
            "INSERT INTO display_overrides(source_instance_id, session_id, display_name, updated_utc) " +
            $"VALUES ('inst_two', 'ses_override', 'Two', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(source_instance_id, session_id, display_name, updated_utc) " +
                $"VALUES ('inst_one', 'ses_override', 'Duplicate', '{Now}');"),
            "UNIQUE");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(session_id, display_name, updated_utc) " +
                $"VALUES ('ses_override', 'Unscoped', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(display_name, updated_utc) " +
                $"VALUES ('No Target', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO display_overrides(project_id, task_id, display_name, updated_utc) " +
                $"VALUES ('prj_override', 'tsk_override', 'Ambiguous', '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(raw, "DELETE FROM tasks WHERE task_id = 'tsk_override';"),
            "FOREIGN KEY");
    }

    [TestMethod]
    public void Provenance_RequiresTypedScopedSubjectAndMatchingAttachment()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_prov");
        InsertTask(raw, "tsk_prov", "prj_prov");
        Execute(raw, "INSERT INTO jobs(job_id, project_id, created_utc) VALUES ('job_prov_a', 'prj_prov', '2026-01-01T00:00:00Z');");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_prov_b', '2026-01-01T00:00:00Z');");
        InsertSession(raw, "inst_prov", "ses_prov");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
                $"VALUES ('job', 'job_missing', 'job_missing', 'forge', '{Now}');"),
            "FOREIGN KEY");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_prov_a', 'job_prov_a', 'forge', '{Now}');");
        var provenanceId = Convert.ToInt64(Scalar(raw, "SELECT max(provenance_id) FROM provenance;"));

        Execute(
            raw,
            "UPDATE jobs SET provenance_id = " + provenanceId + " WHERE job_id = 'job_prov_a';");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE jobs SET provenance_id = " + provenanceId + " WHERE job_id = 'job_prov_b';"),
            "provenance");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO provenance(entity_type, entity_id, source_instance_id, session_id, source_kind, observed_utc) " +
                $"VALUES ('session', 'ses_missing', 'inst_prov', 'ses_missing', 'opencode', '{Now}');"),
            "FOREIGN KEY");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, source_instance_id, session_id, source_kind, observed_utc) " +
            $"VALUES ('session', 'ses_prov', 'inst_prov', 'ses_prov', 'opencode', '{Now}');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE provenance SET session_id = 'ses_missing' WHERE entity_type = 'session';"),
            "immutable");
    }

    [TestMethod]
    public void Validation_RequiresSpecificCompatibleEvidenceForVerification()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_val");
        InsertTask(raw, "tsk_val", "prj_val");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_val', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_val_a', 'tsk_val', 'job_val', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, status, started_utc) " +
            $"VALUES ('run_val_b', 'tsk_val', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_val_a', 'run_val_a', 'passed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_val_b', 'run_val_b', 'passed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO execution_evidence(evidence_id, result_id, evidence_kind, summary, observed_utc) " +
            $"VALUES ('evidence_val_a', 'result_val_a', 'test', 'synthetic', '{Now}');");

        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, validation_kind, validation_authority, status, created_utc) " +
            $"VALUES ('validation_claim', 'result_val_a', 'run_val_a', 'test', 'worker', 'passed', '{Now}');");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO validations(validation_id, result_id, result_run_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
                $"VALUES ('validation_unbacked', 'result_val_a', 'run_val_a', 'test', 'independent', 'passed', 1, '{Now}');"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
                $"VALUES ('validation_wrong_owner', 'result_val_b', 'run_val_b', 'evidence_val_a', 'result_val_b', 'test', 'independent', 'passed', 1, '{Now}');"),
            "FOREIGN KEY");
        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
            $"VALUES ('validation_supported', 'result_val_a', 'run_val_a', 'evidence_val_a', 'result_val_a', 'test', 'independent', 'passed', 1, '{Now}');");
        Assert.AreEqual(
            1L,
            Convert.ToInt64(Scalar(
                raw,
                "SELECT is_verified FROM validations WHERE validation_id = 'validation_supported';")));
    }

    [TestMethod]
    public void DeliveryCuts_AllowCursorAdvancementAndProtectCapturedSnapshot()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_cut_live', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO source_generations(generation_id, job_id, source_path, volume_serial, file_index, creation_utc_ticks, first_seen_utc, last_seen_utc) " +
            $"VALUES (1, 'job_cut_live', 'live.log', 1, '1', 1, '{Now}', '{Now}');");
        Execute(raw, "INSERT INTO source_cursors(generation_id, committed_offset, version) VALUES (1, 10, 0);");
        Execute(
            raw,
            "INSERT INTO monitoring_starts(start_id, started_utc, status) " +
            $"VALUES ('start_live', '{Now}', 'running');");
        Execute(
            raw,
            "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
            $"VALUES ('start_live', 'job_cut_live', 1, 10, 'log', 5, '{Now}');");

        Execute(raw, "UPDATE source_cursors SET committed_offset = 20, version = 1 WHERE generation_id = 1;");
        Assert.AreEqual(
            20L,
            Convert.ToInt64(Scalar(raw, "SELECT committed_offset FROM source_cursors WHERE generation_id = 1;")));
        Assert.AreEqual(
            10L,
            Convert.ToInt64(Scalar(raw, "SELECT committed_offset FROM source_delivery_cuts WHERE start_id = 'start_live';")));
        Execute(raw, "UPDATE source_delivery_cuts SET delivered_offset = 6 WHERE start_id = 'start_live';");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE source_delivery_cuts SET delivered_offset = 11 WHERE start_id = 'start_live';"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE source_delivery_cuts SET committed_offset = 20 WHERE start_id = 'start_live';"),
            "snapshot");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE source_delivery_cuts SET start_id = 'start_other' WHERE start_id = 'start_live';"),
            "snapshot");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
                $"VALUES ('start_live', 'job_cut_live', 1, 21, 'log', 5, '{Now}');"),
            "capture");

        Execute(raw, "BEGIN IMMEDIATE;");
        Execute(raw, "UPDATE source_cursors SET committed_offset = 30, version = 2 WHERE generation_id = 1;");
        Execute(
            raw,
            "INSERT INTO monitoring_starts(start_id, started_utc, status) " +
            $"VALUES ('start_live_2', '{Now}', 'running');");
        Execute(
            raw,
            "INSERT INTO source_delivery_cuts(start_id, job_id, generation_id, committed_offset, delivery_kind, delivered_offset, cut_utc) " +
            $"VALUES ('start_live_2', 'job_cut_live', 1, 30, 'log', 30, '{Now}');");
        Execute(raw, "ROLLBACK;");
        Assert.AreEqual(
            20L,
            Convert.ToInt64(Scalar(raw, "SELECT committed_offset FROM source_cursors WHERE generation_id = 1;")));
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM source_delivery_cuts WHERE start_id = 'start_live_2';")));
    }

    [TestMethod]
    public void Provenance_TypedSubjectsRejectNullBypassAndOrphans()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_prov_all");
        InsertTask(raw, "tsk_prov_all", "prj_prov_all");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_prov_all', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_prov_all', 'tsk_prov_all', 'job_prov_all', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_prov_all', 'run_prov_all', 0, 'running', '{Now}');");
        InsertSession(raw, "inst_prov_all", "ses_prov_all");
        InsertMessage(raw, "inst_prov_all", "ses_prov_all", "msg_prov_all", 0);
        Execute(
            raw,
            "INSERT INTO usage_observations(usage_id, source_instance_id, source_usage_id, usage_scope, authority, session_id, observed_utc) " +
            $"VALUES ('use_prov_all', 'inst_prov_all', 'src_prov_all', 'session', 'reported', 'ses_prov_all', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_prov_all', 'run_prov_all', 'passed', '{Now}');");
        InsertAppliedLogicalEvent(raw, "job_prov_all", "0.10");

        foreach (var sql in new[]
                 {
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('project', 'prj_prov_all', 'manual', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('task', 'tsk_prov_all', 'manual', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('run', 'run_prov_all', 'manual', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('invocation', 'inv_prov_all', 'manual', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('job', 'job_prov_all', 'forge', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('usage', 'use_prov_all', 'opencode', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('logical_event', '1', 'forge', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, logical_event_id, source_kind, observed_utc) VALUES ('logical_event', '01', 1, 'forge', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('result', 'result_prov_all', 'manual', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('session', 'ses_prov_all', 'opencode', '2026-01-01T00:00:00Z');",
                     "INSERT INTO provenance(entity_type, entity_id, source_kind, observed_utc) VALUES ('message', 'msg_prov_all', 'opencode', '2026-01-01T00:00:00Z');"
                 })
        {
            AssertSqliteFailureContaining(() => Execute(raw, sql), "CHECK");
        }

        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, project_id, source_kind, observed_utc) " +
            $"VALUES ('project', 'prj_prov_all', 'prj_prov_all', 'manual', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, task_id, source_kind, observed_utc) " +
            $"VALUES ('task', 'tsk_prov_all', 'tsk_prov_all', 'manual', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, run_id, source_kind, observed_utc) " +
            $"VALUES ('run', 'run_prov_all', 'run_prov_all', 'manual', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, invocation_id, source_kind, observed_utc) " +
            $"VALUES ('invocation', 'inv_prov_all', 'inv_prov_all', 'manual', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_prov_all', 'job_prov_all', 'forge', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, usage_id, source_kind, observed_utc) " +
            $"VALUES ('usage', 'use_prov_all', 'use_prov_all', 'opencode', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, logical_event_id, source_kind, observed_utc) " +
            $"VALUES ('logical_event', '1', 1, 'forge', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, result_id, source_kind, observed_utc) " +
            $"VALUES ('result', 'result_prov_all', 'result_prov_all', 'manual', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, source_instance_id, session_id, source_kind, observed_utc) " +
            $"VALUES ('session', 'ses_prov_all', 'inst_prov_all', 'ses_prov_all', 'opencode', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, source_instance_id, session_id, message_id, source_kind, observed_utc) " +
            $"VALUES ('message', 'msg_prov_all', 'inst_prov_all', 'ses_prov_all', 'msg_prov_all', 'opencode', '{Now}');");

        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM pragma_foreign_key_check;")));
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE provenance SET job_id = 'job_other' WHERE entity_type = 'job';"),
            "immutable");

        Execute(raw, "BEGIN IMMEDIATE;");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, project_id, source_kind, observed_utc) " +
            $"VALUES ('project', 'prj_prov_all', 'prj_prov_all', 'rollback', '{Now}');");
        Execute(raw, "ROLLBACK;");
        Assert.AreEqual(
            0L,
            Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM provenance WHERE source_kind = 'rollback';")));
    }

    [TestMethod]
    public void Validation_ReviewerProvenanceMustMatchCompatibleSubject()
    {
        using (var ledger = CreateLedger())
        {
            Assert.IsNotNull(ledger.DatabasePath);
        }

        using var raw = OpenRawConnection(_databasePath);
        InsertProject(raw, "prj_reviewer");
        InsertTask(raw, "tsk_reviewer", "prj_reviewer");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_reviewer_a', '2026-01-01T00:00:00Z');");
        Execute(raw, "INSERT INTO jobs(job_id, created_utc) VALUES ('job_reviewer_b', '2026-01-01T00:00:00Z');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_reviewer', 'tsk_reviewer', 'job_reviewer_a', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_reviewer_b', 'tsk_reviewer', 'job_reviewer_b', 'running', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_reviewer', 'run_reviewer', 'passed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO results(result_id, run_id, status, created_utc) " +
            $"VALUES ('result_reviewer_b', 'run_reviewer_b', 'passed', '{Now}');");
        Execute(
            raw,
            "INSERT INTO execution_evidence(evidence_id, result_id, evidence_kind, observed_utc) " +
            $"VALUES ('evidence_reviewer', 'result_reviewer', 'test', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_reviewer_a', 'job_reviewer_a', 'forge', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_reviewer_b', 'job_reviewer_b', 'forge', '{Now}');");
        Execute(
            raw,
            "INSERT INTO provenance(entity_type, entity_id, run_id, source_kind, observed_utc) " +
            $"VALUES ('run', 'run_reviewer', 'run_reviewer', 'manual', '{Now}');");
        var matchingJob = Convert.ToInt64(Scalar(
            raw,
            "SELECT provenance_id FROM provenance WHERE entity_type = 'job' AND job_id = 'job_reviewer_a';"));
        var unrelatedJob = Convert.ToInt64(Scalar(
            raw,
            "SELECT provenance_id FROM provenance WHERE entity_type = 'job' AND job_id = 'job_reviewer_b';"));
        var matchingRun = Convert.ToInt64(Scalar(
            raw,
            "SELECT provenance_id FROM provenance WHERE entity_type = 'run';"));

        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, reviewer_provenance_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
            $"VALUES ('validation_reviewer_ok', 'result_reviewer', 'run_reviewer', 'evidence_reviewer', 'result_reviewer', {matchingRun}, 'test', 'independent', 'passed', 1, '{Now}');");
        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, reviewer_provenance_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
            $"VALUES ('validation_job_reviewer_ok', 'result_reviewer', 'run_reviewer', 'evidence_reviewer', 'result_reviewer', {matchingJob}, 'test', 'independent', 'passed', 1, '{Now}');");
        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
            $"VALUES ('validation_manual_ok', 'result_reviewer', 'run_reviewer', 'evidence_reviewer', 'result_reviewer', 'test', 'user_acceptance', 'passed', 1, '{Now}');");
        Execute(
            raw,
            "INSERT INTO validations(validation_id, result_id, result_run_id, validation_kind, validation_authority, status, created_utc) " +
            $"VALUES ('validation_claim_ok', 'result_reviewer', 'run_reviewer', 'test', 'worker', 'passed', '{Now}');");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, reviewer_provenance_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
                $"VALUES ('validation_unrelated_reviewer', 'result_reviewer', 'run_reviewer', 'evidence_reviewer', 'result_reviewer', {unrelatedJob}, 'test', 'independent', 'passed', 1, '{Now}');"),
            "reviewer");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE validations SET reviewer_provenance_id = " + unrelatedJob +
                " WHERE validation_id = 'validation_reviewer_ok';"),
            "reviewer");
        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "UPDATE validations SET result_id = 'result_reviewer_b', result_run_id = 'run_reviewer_b' " +
                "WHERE validation_id = 'validation_job_reviewer_ok';"),
            "reviewer");

        AssertSqliteFailureContaining(
            () => Execute(
                raw,
                "INSERT INTO validations(validation_id, validation_kind, validation_authority, status, created_utc) " +
                $"VALUES ('validation_no_target', 'test', 'worker', 'passed', '{Now}');"),
            "CHECK");
    }

    [TestMethod]
    public void Validation_VerifiedJobReviewerRejectsRunJobChanges()
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "verified", "job", verified: true);
        foreach (var job in new[] { "'job_validation_c'", "NULL" })
        {
            AssertSqliteFailureContaining(
                () => Execute(raw, $"UPDATE task_runs SET job_id = {job} WHERE run_id = 'run_validation_a';"),
                "validation reviewer provenance mismatch");
            Assert.AreEqual("job_validation_a", Scalar(raw, "SELECT job_id FROM task_runs WHERE run_id = 'run_validation_a';"));
        }
    }

    [TestMethod]
    public void Validation_VerifiedInvocationReviewerRejectsInvocationMove()
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "verified", "invocation", verified: true);
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE delegation_invocations SET run_id = 'run_validation_b' WHERE invocation_id = 'inv_validation';"),
            "validation reviewer provenance mismatch");
        Assert.AreEqual("run_validation_a", Scalar(raw, "SELECT run_id FROM delegation_invocations WHERE invocation_id = 'inv_validation';"));
    }

    [DataTestMethod]
    [DataRow("job")]
    [DataRow("invocation")]
    public void Validation_PromotionRechecksCurrentReviewerOwnership(string reviewer)
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "claim", reviewer, verified: false);
        Execute(raw, reviewer == "job"
            ? "UPDATE task_runs SET job_id = NULL WHERE run_id = 'run_validation_a';"
            : "UPDATE delegation_invocations SET run_id = 'run_validation_b' WHERE invocation_id = 'inv_validation';");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE validations SET is_verified = 1 WHERE validation_id = 'claim';"),
            "validation reviewer provenance mismatch");
        Assert.AreEqual(0L, Convert.ToInt64(Scalar(raw, "SELECT is_verified FROM validations WHERE validation_id = 'claim';")));
        Execute(raw, reviewer == "job"
            ? "UPDATE task_runs SET job_id = 'job_validation_a' WHERE run_id = 'run_validation_a';"
            : "UPDATE delegation_invocations SET run_id = 'run_validation_a' WHERE invocation_id = 'inv_validation';");
        Execute(raw, "UPDATE validations SET is_verified = 1 WHERE validation_id = 'claim';");
        Assert.AreEqual(1L, Convert.ToInt64(Scalar(raw, "SELECT is_verified FROM validations WHERE validation_id = 'claim';")));
    }

    [DataTestMethod]
    [DataRow("job")]
    [DataRow("invocation")]
    [DataRow("provenance")]
    [DataRow("provenance_update")]
    [DataRow("run_update")]
    [DataRow("invocation_update")]
    public void Validation_ReplacementCannotBypassReviewerOwnership(string parent)
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "verified", parent.StartsWith("invocation", StringComparison.Ordinal) ? "invocation" : "job", verified: true);
        Execute(raw, "INSERT INTO task_runs(run_id, task_id, status, started_utc) " +
            $"VALUES ('run_validation_spare', 'tsk_validation', 'running', '{Now}');");
        Execute(raw, "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_validation_spare', 'run_validation_b', 1, 'running', '{Now}');");
        var sql = parent switch
        {
            "job" => "INSERT OR REPLACE INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
                $"VALUES ('run_validation_a', 'tsk_validation', NULL, 'running', '{Now}');",
            "invocation" => "INSERT OR REPLACE INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
                $"VALUES ('inv_validation', 'run_validation_b', 0, 'running', '{Now}');",
            "provenance" => "INSERT OR REPLACE INTO provenance(provenance_id, entity_type, entity_id, job_id, source_kind, observed_utc) " +
                $"VALUES (1, 'job', 'job_validation_b', 'job_validation_b', 'replacement', '{Now}');",
            "run_update" => "UPDATE OR REPLACE task_runs SET run_id = 'run_validation_a', job_id = NULL WHERE run_id = 'run_validation_spare';",
            "invocation_update" => "UPDATE OR REPLACE delegation_invocations SET invocation_id = 'inv_validation' WHERE invocation_id = 'inv_validation_spare';",
            _ => "UPDATE OR REPLACE provenance SET provenance_id = 1 WHERE provenance_id = 4;"
        };
        AssertSqliteFailureContaining(() => Execute(raw, sql), "validation reviewer provenance mismatch");
        Assert.AreEqual("job_validation_a", Scalar(raw, "SELECT job_id FROM task_runs WHERE run_id = 'run_validation_a';"));
        Assert.AreEqual("run_validation_a", Scalar(raw, "SELECT run_id FROM delegation_invocations WHERE invocation_id = 'inv_validation';"));
        Assert.AreEqual("job_validation_a", Scalar(raw, "SELECT job_id FROM provenance WHERE provenance_id = 1;"));
        Assert.AreEqual(0, QueryStrings(raw, "PRAGMA foreign_key_check;").Count);
    }

    [TestMethod]
    public void Validation_CompatibleParentReplacementsAndOwnershipChangesRemainValid()
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "job_verified", "job", verified: true);
        InsertOwnershipValidation(raw, "inv_verified", "invocation", verified: true);
        Execute(raw, "INSERT OR REPLACE INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
            $"VALUES ('run_validation_a', 'tsk_validation', 'job_validation_a', 'completed', '{Now}');");
        Execute(raw, "INSERT OR REPLACE INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_validation', 'run_validation_a', 0, 'completed', '{Now}');");
        Execute(raw, "INSERT OR REPLACE INTO provenance(provenance_id, entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES (1, 'job', 'job_validation_a', 'job_validation_a', 'replacement', '{Now}');");
        Execute(raw, "UPDATE validations SET reviewer_provenance_id = 3 WHERE validation_id = 'job_verified';");
        Execute(raw, "UPDATE task_runs SET job_id = 'job_validation_c' WHERE run_id = 'run_validation_a';");
        Execute(raw, "UPDATE task_runs SET job_id = NULL WHERE run_id = 'run_validation_a';");
        Assert.AreEqual(2L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM validations WHERE is_verified = 1;")));
        Assert.AreEqual(0, QueryStrings(raw, "PRAGMA foreign_key_check;").Count);
    }

    [DataTestMethod]
    [DataRow("run")]
    [DataRow("invocation")]
    [DataRow("provenance")]
    public void Validation_ConflictNoOpsAndCompatibleUpsertsRemainValid(string parent)
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "job_verified", "job", verified: true);
        InsertOwnershipValidation(raw, "inv_verified", "invocation", verified: true);
        // The proposed INSERT ownership is discarded on conflict. Validate only
        // the row that is actually inserted or updated, not the discarded values.
        var insert = parent switch
        {
            "run" => "INSERT INTO task_runs(run_id, task_id, status, started_utc) " +
                $"VALUES ('run_validation_a', 'tsk_validation', 'completed', '{Now}')",
            "invocation" => "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
                $"VALUES ('inv_validation', 'run_validation_b', 0, 'completed', '{Now}')",
            _ => "INSERT INTO provenance(provenance_id, entity_type, entity_id, job_id, source_kind, observed_utc) " +
                $"VALUES (1, 'job', 'job_validation_b', 'job_validation_b', 'replacement', '{Now}')"
        };
        var key = parent switch { "run" => "run_id", "invocation" => "invocation_id", _ => "provenance_id" };
        var update = parent == "provenance" ? "source_ref = 'safe-upsert'" : "status = 'completed'";
        Execute(raw, insert + $" ON CONFLICT({key}) DO NOTHING;");
        Execute(raw, insert + $" ON CONFLICT({key}) DO UPDATE SET {update};");
        Assert.AreEqual("job_validation_a", Scalar(raw, "SELECT job_id FROM task_runs WHERE run_id = 'run_validation_a';"));
        Assert.AreEqual("run_validation_a", Scalar(raw, "SELECT run_id FROM delegation_invocations WHERE invocation_id = 'inv_validation';"));
        Assert.AreEqual("job_validation_a", Scalar(raw, "SELECT job_id FROM provenance WHERE provenance_id = 1;"));
        Assert.AreEqual(2L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM validations WHERE is_verified = 1;")));
    }

    [TestMethod]
    public void Validation_ReferencedEvidenceAndOwnershipChainRejectInvalidMutation()
    {
        using var raw = CreateValidationOwnershipFixture();
        InsertOwnershipValidation(raw, "job_verified", "job", verified: true);
        InsertOwnershipValidation(raw, "inv_verified", "invocation", verified: true);
        foreach (var sql in new[]
                 {
                     "UPDATE results SET run_id = 'run_validation_b' WHERE result_id = 'result_validation_a';",
                     "UPDATE execution_evidence SET result_id = 'result_validation_b' WHERE evidence_id = 'evidence_validation_a';",
                     "DELETE FROM results WHERE result_id = 'result_validation_a';",
                     "DELETE FROM execution_evidence WHERE evidence_id = 'evidence_validation_a';",
                     "DELETE FROM task_runs WHERE run_id = 'run_validation_a';",
                     "DELETE FROM jobs WHERE job_id = 'job_validation_a';",
                     "DELETE FROM delegation_invocations WHERE invocation_id = 'inv_validation';",
                     "DELETE FROM provenance WHERE provenance_id = 1;",
                     "UPDATE task_runs SET run_id = 'renamed' WHERE run_id = 'run_validation_a';",
                     "UPDATE delegation_invocations SET invocation_id = 'renamed' WHERE invocation_id = 'inv_validation';",
                     "UPDATE provenance SET provenance_id = 99 WHERE provenance_id = 1;"
                 })
        {
            AssertSqliteFailureContaining(() => Execute(raw, sql), "FOREIGN KEY");
        }
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE provenance SET entity_id = 'job_validation_b', job_id = 'job_validation_b' WHERE provenance_id = 1;"),
            "provenance subject immutable");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE validations SET evidence_id = NULL, evidence_result_id = NULL WHERE validation_id = 'job_verified';"),
            "CHECK");
        AssertSqliteFailureContaining(
            () => Execute(raw, "UPDATE validations SET evidence_id = 'evidence_validation_b', evidence_result_id = 'result_validation_b' WHERE validation_id = 'job_verified';"),
            "CHECK");
        Assert.AreEqual(2L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM validations WHERE is_verified = 1;")));
        Assert.AreEqual(0, QueryStrings(raw, "PRAGMA foreign_key_check;").Count);
    }

    [TestMethod]
    public void Validation_CompatibleUpdatesAndExplicitTransactionalReassignmentSurviveReopen()
    {
        using (var raw = CreateValidationOwnershipFixture())
        {
            InsertOwnershipValidation(raw, "job_verified", "job", verified: true);
            InsertOwnershipValidation(raw, "inv_verified", "invocation", verified: true);
            Execute(raw, "UPDATE task_runs SET status = 'completed', job_id = job_id WHERE run_id = 'run_validation_a';");
            Execute(raw, "UPDATE delegation_invocations SET model = 'synthetic', run_id = run_id WHERE invocation_id = 'inv_validation';");
            Execute(raw, "UPDATE provenance SET source_ref = 'reviewed-local-evidence' WHERE provenance_id = 1;");
            Execute(raw, "UPDATE execution_evidence SET summary = 'recorded test output' WHERE evidence_id = 'evidence_validation_a';");
            Execute(raw, "UPDATE results SET status = 'partial' WHERE result_id = 'result_validation_a';");
            Execute(raw, "UPDATE validations SET reviewer_provenance_id = 3 WHERE validation_id = 'job_verified';");
            Execute(raw, "UPDATE validations SET reviewer_provenance_id = 1 WHERE validation_id = 'job_verified';");

            // Immediate constraints require dependent validations to be explicitly retired
            // before parent reassignment. A failed statement does not roll back a transaction.
            Execute(raw, "BEGIN;");
            Execute(raw, "UPDATE task_runs SET status = 'failed' WHERE run_id = 'run_validation_a';");
            AssertSqliteFailureContaining(
                () => Execute(raw, "UPDATE task_runs SET job_id = NULL WHERE run_id = 'run_validation_a';"),
                "validation reviewer provenance mismatch");
            Execute(raw, "ROLLBACK;");
            Assert.AreEqual("completed", Scalar(raw, "SELECT status FROM task_runs WHERE run_id = 'run_validation_a';"));

            Execute(raw, "BEGIN;");
            Execute(raw, "DELETE FROM validations;");
            Execute(raw, "UPDATE task_runs SET job_id = NULL WHERE run_id = 'run_validation_a';");
            Execute(raw, "UPDATE delegation_invocations SET run_id = 'run_validation_b' WHERE invocation_id = 'inv_validation';");
            Execute(raw, "ROLLBACK;");
            Assert.AreEqual(2L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM validations WHERE is_verified = 1;")));

            Execute(raw, "BEGIN;");
            Execute(raw, "DELETE FROM validations;");
            Execute(raw, "UPDATE task_runs SET job_id = NULL WHERE run_id = 'run_validation_a';");
            Execute(raw, "UPDATE delegation_invocations SET run_id = 'run_validation_b' WHERE invocation_id = 'inv_validation';");
            InsertOwnershipValidation(raw, "moved_invocation", "invocation", verified: true, target: "b");
            Execute(raw, "COMMIT;");
            Execute(raw, $"INSERT INTO validations(validation_id, run_id, validation_kind, validation_authority, status, created_utc) VALUES ('worker_claim', 'run_validation_a', 'review', 'worker', 'unknown', '{Now}');");
        }
        using var reopened = CreateLedger();
        using var check = OpenRawConnection(_databasePath);
        Assert.AreEqual("run_validation_b", Scalar(check, "SELECT result_run_id FROM validations WHERE validation_id = 'moved_invocation';"));
        Assert.AreEqual(0L, Convert.ToInt64(Scalar(check, "SELECT is_verified FROM validations WHERE validation_id = 'worker_claim';")));
        Assert.AreEqual(0, QueryStrings(check, "PRAGMA foreign_key_check;").Count);
    }

    private SqliteConnection CreateValidationOwnershipFixture()
    {
        using (var ledger = CreateLedger()) { }
        var raw = OpenRawConnection(_databasePath);
        Assert.AreEqual(1L, Convert.ToInt64(Scalar(raw, "PRAGMA foreign_keys;")));
        InsertProject(raw, "prj_validation");
        InsertTask(raw, "tsk_validation", "prj_validation");
        Execute(raw, $"INSERT INTO jobs(job_id, created_utc) VALUES ('job_validation_c', '{Now}');");
        foreach (var suffix in new[] { "a", "b" })
        {
            Execute(raw, $"INSERT INTO jobs(job_id, created_utc) VALUES ('job_validation_{suffix}', '{Now}');");
            Execute(raw, "INSERT INTO task_runs(run_id, task_id, job_id, status, started_utc) " +
                $"VALUES ('run_validation_{suffix}', 'tsk_validation', 'job_validation_{suffix}', 'running', '{Now}');");
            Execute(raw, "INSERT INTO results(result_id, run_id, status, created_utc) " +
                $"VALUES ('result_validation_{suffix}', 'run_validation_{suffix}', 'passed', '{Now}');");
            Execute(raw, "INSERT INTO execution_evidence(evidence_id, result_id, evidence_kind, observed_utc) " +
                $"VALUES ('evidence_validation_{suffix}', 'result_validation_{suffix}', 'test', '{Now}');");
        }
        Execute(raw, "INSERT INTO delegation_invocations(invocation_id, run_id, invocation_index, status, started_utc) " +
            $"VALUES ('inv_validation', 'run_validation_a', 0, 'completed', '{Now}');");
        Execute(raw, "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_validation_a', 'job_validation_a', 'forge', '{Now}');");
        Execute(raw, "INSERT INTO provenance(entity_type, entity_id, invocation_id, source_kind, observed_utc) " +
            $"VALUES ('invocation', 'inv_validation', 'inv_validation', 'manual', '{Now}');");
        Execute(raw, "INSERT INTO provenance(entity_type, entity_id, run_id, source_kind, observed_utc) " +
            $"VALUES ('run', 'run_validation_a', 'run_validation_a', 'manual', '{Now}');");
        Execute(raw, "INSERT INTO provenance(entity_type, entity_id, job_id, source_kind, observed_utc) " +
            $"VALUES ('job', 'job_validation_b', 'job_validation_b', 'forge', '{Now}');");
        return raw;
    }

    private static void InsertOwnershipValidation(
        SqliteConnection raw, string id, string reviewer, bool verified, string target = "a") =>
        Execute(raw, "INSERT INTO validations(validation_id, result_id, result_run_id, evidence_id, evidence_result_id, reviewer_provenance_id, validation_kind, validation_authority, status, is_verified, created_utc) " +
            $"VALUES ('{id}', 'result_validation_{target}', 'run_validation_{target}', 'evidence_validation_{target}', 'result_validation_{target}', {(reviewer == "job" ? 1 : 2)}, 'review', 'independent', 'passed', {(verified ? 1 : 0)}, '{Now}');");

    private static void InsertAppliedLogicalEvent(
        SqliteConnection connection,
        string jobId,
        string costText)
    {
        Execute(
            connection,
            "INSERT INTO source_generations(job_id, source_path, volume_serial, file_index, creation_utc_ticks, first_seen_utc, last_seen_utc) " +
            $"VALUES ('{jobId}', 'synthetic.log', 1, '1', 1, '{Now}', '{Now}');");
        Execute(
            connection,
            "INSERT INTO source_records(generation_id, start_byte, end_byte, payload_length, disposition, content_sha256, committed_utc) " +
            $"VALUES (1, 0, 2, 1, 'Accepted', 'sha', '{Now}');");
        Execute(
            connection,
            "INSERT INTO source_events(record_id, job_id, event_key, observed_at_utc, category, severity, summary) " +
            $"VALUES (1, '{jobId}', 'event_key', '{Now}', 'Progress', 'Info', 'synthetic');");
        Execute(
            connection,
            "INSERT INTO logical_events(source_event_id, job_id, decision, cost_text, cost_expected, observed_at_utc, applied_utc) " +
            $"VALUES (1, '{jobId}', 'Applied', '{costText}', 1, '{Now}', '{Now}');");
    }

    private EventLedger CreateLedger(EventLedgerOptions? options = null) =>
        EventLedger.OpenV3Projection(_databasePath, _workspace.Root, options);

    private static SqliteConnection OpenRawConnection(string databasePath, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        if (!readOnly)
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
        }

        return connection;
    }

    private static void InsertProject(SqliteConnection connection, string projectId) =>
        Execute(
            connection,
            "INSERT INTO projects(project_id, display_name, created_utc, updated_utc) " +
            $"VALUES ('{projectId}', '{projectId}', '{Now}', '{Now}');");

    private static void InsertTask(SqliteConnection connection, string taskId, string projectId) =>
        Execute(
            connection,
            "INSERT INTO tasks(task_id, project_id, status, created_utc, updated_utc) " +
            $"VALUES ('{taskId}', '{projectId}', 'active', '{Now}', '{Now}');");

    private static void InsertSession(
        SqliteConnection connection,
        string sourceInstanceId,
        string sessionId,
        string? parentSessionId = null) =>
        Execute(
            connection,
            "INSERT INTO opencode_sessions(source_instance_id, session_id, parent_session_id, status, started_utc, updated_utc) " +
            $"VALUES ('{sourceInstanceId}', '{sessionId}', " +
            $"{(parentSessionId is null ? "NULL" : $"'{parentSessionId}'")}, 'active', '{Now}', '{Now}');");

    private static void InsertMessage(
        SqliteConnection connection,
        string sourceInstanceId,
        string sessionId,
        string messageId,
        int sequence) =>
        Execute(
            connection,
            "INSERT INTO messages(source_instance_id, session_id, message_id, role, sequence, created_utc) " +
            $"VALUES ('{sourceInstanceId}', '{sessionId}', '{messageId}', 'assistant', {sequence}, '{Now}');");

    private static void InsertSegment(
        SqliteConnection connection,
        string sourceInstanceId,
        string segmentId,
        string sessionId,
        string? runId) =>
        Execute(
            connection,
            "INSERT INTO execution_segments(source_instance_id, segment_id, session_id, run_id, status, started_utc) " +
            $"VALUES ('{sourceInstanceId}', '{segmentId}', '{sessionId}', " +
            $"{(runId is null ? "NULL" : $"'{runId}'")}, 'running', '{Now}');");

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> QueryStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void AssertLedgerFailure(
        LedgerStorageFailure expected,
        Action action)
    {
        try
        {
            action();
            Assert.Fail("The V3 ledger operation should have failed closed.");
        }
        catch (LedgerException exception)
        {
            Assert.AreEqual(expected, exception.Failure);
        }
    }

    private static void AssertSqliteFailure(Action action)
    {
        try
        {
            action();
            Assert.Fail("The database constraint should have rejected the write.");
        }
        catch (SqliteException)
        {
        }
    }

    private static void AssertSqliteFailureContaining(Action action, string expected)
    {
        try
        {
            action();
            Assert.Fail("The database constraint should have rejected the write.");
        }
        catch (SqliteException exception)
        {
            Assert.IsTrue(
                exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"Expected SQLite failure containing '{expected}', but observed: {exception.Message}");
        }
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string databasePath)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                files[path] = ReadAllBytesShared(path);
            }
        }

        return files;
    }

    private static void AssertPreserved(
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        string databasePath)
    {
        Assert.IsTrue(File.Exists(databasePath));
        foreach (var pair in expectedFiles)
        {
            Assert.IsTrue(
                File.Exists(pair.Key),
                $"Expected preserved database file '{pair.Key}' was removed.");
            CollectionAssert.AreEqual(
                pair.Value,
                ReadAllBytesShared(pair.Key),
                $"Expected preserved database file '{pair.Key}' to remain byte-for-byte unchanged.");
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
