using System.Text;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class JobLiveActivityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_010_000);
    private static readonly LedgerSourceIdentity SyntheticIdentityB =
        new(0x12345678, 0x1122334455667788, 0x01D0000000000000);

    [TestMethod]
    public void SharedSession_FourJobsKeepDistinctMessagesAndResults()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 4; index++)
        {
            workspace.WriteMetadata("job" + index);
            workspace.WriteLog("job" + index, Text("result " + index, "msg" + index), Finish("msg" + index));
        }
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        foreach (var job in jobs)
        {
            var index = job.JobId[^1];
            var activity = Read(workspace, job.JobId);
            Assert.AreEqual("result " + index, activity.Doing?.Text);
            Assert.AreEqual("result " + index, activity.Done?.Text);
            Assert.AreEqual("msg" + index, activity.Done?.MessageId);
            Assert.AreEqual("shared-session", activity.Done?.SessionId);
            Assert.AreEqual(job.JobId, activity.Done?.JobId);
            StringAssert.Contains(activity.Done!.Kind, "unverified");
        }
    }

    [TestMethod]
    public void Done_RequiresSameMessageAndExplicitStop_NeverInfersFromStatusOrToolSuccess()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job", status: "DONE");
        workspace.WriteLog("job", Text("interim", "message-a"), Finish("message-b"),
            Finish("message-a", "tool-calls"), ToolUse(1_800_000_001_000,
                "shell", "completed", 1_800_000_000_000, 1_800_000_001_000));
        var activity = Read(workspace);
        Assert.IsNull(activity.Done);
        StringAssert.Contains(activity.Doing!.Text, "completed");
        workspace.AppendLog("job", Finish("message-a"));
        Assert.AreEqual("interim", Read(workspace).Done?.Text);
    }

    [TestMethod]
    public void LatestTextAndTool_UsePhysicalSourceOrderAndRetainLastReportedResult()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("reported", "final"), Finish("final"),
            Text("later activity with older timestamp", "next", timestamp: 1_000));
        var activity = Read(workspace);
        Assert.AreEqual("later activity with older timestamp", activity.Doing?.Text);
        Assert.AreEqual("reported", activity.Done?.Text);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_000), activity.Doing?.SourceTimestamp);
        Assert.IsTrue(activity.Doing!.ByteOffset > activity.Done!.ByteOffset);
        Assert.AreEqual(Now, activity.Doing.ObservedAt);
    }

    [TestMethod]
    public void Attribution_RejectsMissingIdentity_UserRole_AndFallbackMetadata()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("no message", null), Text("no session", "m", session: null),
            Text("user request", "user", role: "user"));
        Assert.IsNull(Read(workspace).Doing);
        workspace.WriteRawMetadata("job.json", "{\"id\":\"job\"}");
        File.Delete(workspace.LogPath("job"));
        workspace.WriteLog("job", Text("unproven source", "m"));
        var unavailable = Read(workspace);
        Assert.IsNull(unavailable.Doing);
        StringAssert.Contains(unavailable.Availability, "explicit metadata");
    }

    [TestMethod]
    public void AmbiguousSource_AndOutsideRoot_AreNotAttributed()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteMetadata("other", logPath: workspace.LogPath("job"));
        workspace.WriteLog("job", Text("private", "m"));
        var ambiguous = JobLiveActivityReader.Preflight(
            new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs, workspace.RunsRoot);
        CollectionAssert.Contains(ambiguous["job"].DuplicateOwners.ToArray(), "other");
        Assert.IsNull(Read(workspace).Doing);
        workspace.WriteMetadata("job", logPath: workspace.OutsidePath);
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        Assert.IsFalse(jobs.Single(job => job.JobId == "job").HasExplicitLogPath);
        Assert.IsNull(Read(workspace).Doing);
    }

    [TestMethod]
    public void BoundedWindow_SkipsOversizeMalformedAndUnterminatedRecords()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text(new string('x', JobLiveActivityReader.MaxReadBytes * 2), "huge"),
            "not-json", Text("latest complete", "m"));
        File.AppendAllText(workspace.LogPath("job"), Text("partial", "unfinished"));
        var activity = Read(workspace);
        Assert.AreEqual("latest complete", activity.Doing?.Text);
        Assert.IsTrue(activity.Doing!.ByteOffset > JobLiveActivityReader.MaxReadBytes);
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void MissingTruncatedOrReplacedSources_ClearOldContent_AndRestartHasNoStoredExcerpt()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("old result", "old"), Finish("old"));
        var old = Prepare(workspace);
        Assert.IsNotNull(ReadPrepared(workspace, old).Done);
        // In-place shrink is below the captured boundary and must clear content.
        File.WriteAllText(workspace.LogPath("job"), string.Empty);
        var truncated = ReadPrepared(workspace, old);
        Assert.IsNull(truncated.Doing);
        StringAssert.Contains(truncated.Availability, "below the captured boundary");
        // A true path replacement creates a new generation and new content.
        File.Delete(workspace.LogPath("job"));
        workspace.WriteLog("job", Text("replacement", "new"));
        Assert.AreEqual("replacement", Read(workspace).Doing?.Text);
        Assert.IsNull(Read(workspace).Done);
        // Move within the synthetic workspace; no actual user data is touched.
        var present = Prepare(workspace);
        File.Move(workspace.LogPath("job"), Path.Combine(workspace.RunsRoot, "removed.log"));
        var missing = ReadPrepared(workspace, present);
        Assert.IsNull(missing.Doing);
        StringAssert.Contains(missing.Availability, "unavailable");
    }

    [TestMethod]
    public void Excerpts_BoundAndSanitizeControlsBidiAndSupplementaryCharacters()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("<script>literal</script>\u001b\u202e\n" + string.Concat(Enumerable.Repeat("😀", 700)), "m"));
        var text = Read(workspace).Doing!.Text;
        Assert.IsTrue(text.Length <= JobLiveActivityReader.MaxExcerptLength);
        Assert.IsFalse(text.Any(character => char.IsControl(character) || character == '\u202e'));
        StringAssert.StartsWith(text, "<script>literal</script>");
        Assert.IsFalse(char.IsHighSurrogate(text[^1]));
    }

    [TestMethod]
    public void ConflictingRoleOrTypeSignals_AreNotAttributed()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job",
            JsonSerializer.Serialize(new
            {
                type = "text", sessionID = "shared-session", timestamp = 1_800_000_000_000,
                role = "user",
                part = new { id = "text-a", messageID = "a", text = "conflicting role", role = "assistant" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "text", sessionID = "shared-session", timestamp = 1_800_000_000_000,
                part = new { id = "text-b", messageID = "b", type = "tool", text = "conflicting type" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "tool_use", sessionID = "shared-session", timestamp = 1_800_000_000_000,
                part = new { id = "text-c", messageID = "c", type = "text", text = "conflicting tool text" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "text", sessionID = "shared-session", timestamp = 1_800_000_000_000,
                role = 123,
                part = new { id = "text-d", messageID = "d", text = "malformed role" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "text", sessionID = "shared-session", timestamp = 1_800_000_000_000,
                part = new { id = "text-e", messageID = "e", text = "malformed part role", role = 456 }
            }));
        Assert.IsNull(Read(workspace).Doing);
    }

    [TestMethod]
    public void SyntheticUserMessageWithoutAssistantRole_IsNeverAttributedAsDoingOrDone()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job",
            Text("user prompt", "user-1", role: null),
            Finish("user-1", role: null));
        var activity = Read(workspace);
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void RoleEvidence_RequiresExplicitAssistantAndAgreement()
    {
        var negative = new (string Name, string[] RootRoles, string[] PartRoles)[]
        {
            ("absent", Array.Empty<string>(), Array.Empty<string>()),
            ("null-only", new[] { "null" }, new[] { "null" }),
            ("user", new[] { "\"user\"" }, Array.Empty<string>()),
            ("unknown", new[] { "\"system\"" }, Array.Empty<string>()),
            ("malformed", new[] { "123" }, Array.Empty<string>()),
            ("malformed-part", Array.Empty<string>(), new[] { "456" }),
            ("conflict", new[] { "\"assistant\"" }, new[] { "\"user\"" }),
            ("duplicate-same", new[] { "\"assistant\"", "\"assistant\"" }, Array.Empty<string>()),
            ("duplicate-conflict", new[] { "\"assistant\"", "\"user\"" }, Array.Empty<string>())
        };
        foreach (var scenario in negative)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job",
                RoleRecord("text", "m", "payload", scenario.RootRoles, scenario.PartRoles),
                RoleRecord("step_finish", "m", null, scenario.RootRoles, scenario.PartRoles));
            var activity = Read(workspace);
            Assert.IsNull(activity.Doing, scenario.Name);
            Assert.IsNull(activity.Done, scenario.Name);
        }

        var positive = new (string Name, string[] RootRoles, string[] PartRoles)[]
        {
            ("part-assistant", Array.Empty<string>(), new[] { "\"assistant\"" }),
            ("root-assistant", new[] { "\"assistant\"" }, Array.Empty<string>()),
            ("both-assistant", new[] { "\"assistant\"" }, new[] { "\"assistant\"" }),
            ("null-root-part-assistant", new[] { "null" }, new[] { "\"assistant\"" })
        };
        foreach (var scenario in positive)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job",
                RoleRecord("text", "m", "payload", scenario.RootRoles, scenario.PartRoles),
                RoleRecord("step_finish", "m", null, scenario.RootRoles, scenario.PartRoles));
            var activity = Read(workspace);
            Assert.AreEqual("payload", activity.Doing?.Text, scenario.Name);
            Assert.AreEqual("payload", activity.Done?.Text, scenario.Name);
        }
    }

    [TestMethod]
    public void UnknownRootWrapperOrPartType_IsNotAttributed()
    {
        foreach (var record in new[]
                 {
                     RoleRecord(
                         "mystery-wrapper", "m", "unknown wrapper",
                         new[] { "\"assistant\"" }, Array.Empty<string>()),
                     RoleRecord(
                         "text", "m", "unknown part", new[] { "\"assistant\"" },
                         Array.Empty<string>(), partType: "mystery-part")
                 })
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job", record);
            Assert.IsNull(Read(workspace).Doing);
        }
    }

    [TestMethod]
    public void PartCamelCaseIdentity_IsAcceptedWhenRootSessionIsAbsent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", JsonSerializer.Serialize(new
        {
            type = "text", timestamp = 1_800_000_000_000, role = "assistant",
            part = new
            {
                id = "text-m", sessionId = "camel-session", messageId = "camel-message",
                type = "text", text = "camel case"
            }
        }));
        var activity = Read(workspace).Doing;
        Assert.AreEqual("camel case", activity?.Text);
        Assert.AreEqual("camel-session", activity?.SessionId);
        Assert.AreEqual("camel-message", activity?.MessageId);
    }

    [TestMethod]
    public void InvalidUtf8Record_IsSkipped_AndLaterValidRecordIsShown()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var invalid = new byte[] { (byte)'{', 0xFF, (byte)'}', (byte)'\n' };
        var valid = Encoding.UTF8.GetBytes(Text("survives", "m") + "\n");
        File.WriteAllBytes(workspace.LogPath("job"), invalid.Concat(valid).ToArray());
        Assert.AreEqual("survives", Read(workspace).Doing?.Text);
    }

    [TestMethod]
    public void CaseVariantDuplicateSource_IsAmbiguous_ForAllClaimants()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteMetadata("other", logPath: workspace.LogPath("job").ToUpperInvariant());
        workspace.WriteLog("job", Text("private", "m"));
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        Assert.AreEqual(2, jobs.Count);
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        CollectionAssert.Contains(evidence["job"].DuplicateOwners.ToArray(), "other");
        foreach (var job in jobs)
        {
            Assert.IsNull(Read(workspace, job.JobId).Doing, job.JobId);
        }
    }

    [TestMethod]
    public void IngestAThenSameSizePathReplacementB_IsRejected()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("alpha result", "m"), Finish("m"));
        var prepared = Prepare(workspace);
        var first = File.ReadAllText(workspace.LogPath("job"));
        var replacementText = Text("bravo result", "m") + "\n" + Finish("m") + "\n";
        Assert.AreEqual(first.Length, replacementText.Length);
        var replacement = Path.Combine(workspace.RunsRoot, "same-size.tmp");
        File.WriteAllText(replacement, replacementText);
        File.Move(replacement, workspace.LogPath("job"), overwrite: true);
        var activity = ReadPrepared(workspace, prepared);
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void PreflightAThenIngestB_IsRejected()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("identity", "m"), Finish("m"));
        var prepared = Prepare(workspace, identityReader: _ => SyntheticIdentityB);
        Assert.AreEqual(SyntheticIdentityB, prepared.Observation.Identity);
        Assert.AreNotEqual(SyntheticIdentityB, prepared.Observation.PhysicalIdentity);
        Assert.IsFalse(prepared.Observation.IsConsistent);
        var activity = ReadPrepared(workspace, prepared);
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void WeakOrMissingSourceIdentity_ProducesNoObservation()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("weak", "m"));
        var job = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs.Single();
        var weak = Ingest(workspace, job, _ => default);
        Assert.AreEqual(LedgerIngestStatus.AmbiguousSourceIdentity, weak.Status);
        Assert.IsNull(weak.SourceObservation);

        File.Delete(workspace.LogPath("job"));
        var missing = Ingest(workspace, job);
        Assert.AreEqual(LedgerIngestStatus.SourceUnavailable, missing.Status);
        Assert.IsNull(missing.SourceObservation);
    }

    [TestMethod]
    public void WrongJobOrSourceObservation_IsRejected()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("job content", "m"));
        var job = Prepare(workspace, "job");
        workspace.WriteMetadata("other");
        workspace.WriteLog("other", Text("other content", "m2"));
        var other = Prepare(workspace, "other");

        Assert.IsNull(JobLiveActivityReader.Read(
            other.Job, other.Evidence, job.Observation, workspace.RunsRoot, Now).Doing);
        var wrongSource = job.Observation with { SourcePath = workspace.LogPath("other") };
        Assert.IsNull(JobLiveActivityReader.Read(
            job.Job, job.Evidence, wrongSource, workspace.RunsRoot, Now).Doing);
    }

    [TestMethod]
    public void TruncationDuringRead_DiscardsExcerpts()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("before truncate", "m"));
        var prepared = Prepare(workspace);
        var activity = ReadPrepared(workspace, prepared, beforeContentRead: () =>
        {
            using var stream = new FileStream(
                workspace.LogPath("job"),
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(0);
        });
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
        StringAssert.Contains(activity.Availability, "read completely");
    }

    [TestMethod]
    public void PathReplacementDuringRead_DiscardsExcerpts()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("before replacement", "m"));
        var prepared = Prepare(workspace);
        var replacement = Path.Combine(workspace.RunsRoot, "during-read.tmp");
        var activity = ReadPrepared(workspace, prepared, beforePublishCheck: () =>
        {
            File.WriteAllText(replacement, Text("after replacement", "m") + "\n");
            File.Delete(workspace.LogPath("job"));
            File.Move(replacement, workspace.LogPath("job"));
        });
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
        StringAssert.Contains(activity.Availability, "changed before publication");
    }

    [TestMethod]
    public void NoDataObservation_RemainsUsable()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("stable", "m"));
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single();
        var first = Ingest(workspace, job);
        Assert.AreEqual(LedgerIngestStatus.Committed, first.Status);
        Assert.IsNotNull(first.SourceObservation);
        var second = Ingest(workspace, job);
        Assert.AreEqual(LedgerIngestStatus.NoData, second.Status);
        Assert.IsNotNull(second.SourceObservation);
        var activity = JobLiveActivityReader.Read(
            job, evidence["job"], second.SourceObservation!, workspace.RunsRoot, Now);
        Assert.AreEqual("stable", activity.Doing?.Text);
    }

    [TestMethod]
    public void ReplacementCreatesNewGeneration_AndShowsNewContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("old", "m"));
        var first = Prepare(workspace);
        File.Delete(workspace.LogPath("job"));
        workspace.WriteLog("job", Text("new", "m2"));
        var second = Prepare(workspace);
        Assert.AreNotEqual(first.Observation.GenerationId, second.Observation.GenerationId);
        var activity = ReadPrepared(workspace, second);
        Assert.AreEqual("new", activity.Doing?.Text);
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void NonAsciiSourceFilename_IsAttributed()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.RunsRoot, "journal-é.log");
        workspace.WriteMetadata("job", logPath: path);
        File.WriteAllText(path, Text("unicode content", "m") + "\n");
        var activity = Read(workspace);
        Assert.AreEqual("unicode content", activity.Doing?.Text);
        StringAssert.Contains(activity.Doing!.SourcePath, "journal-é.log");
    }

    [TestMethod]
    public void Cancellation_StopsReadBeforeOpeningContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("content", "m"));
        var prepared = Prepare(workspace);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            cancellation.Token));
    }

    [TestMethod]
    public void HardLinkedSource_IsNotAttributedEvenWhenAliasHasNoMetadata()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("aliased content", "m"));
        Assert.IsTrue(ReparsePointFixture.TryCreateHardLink(
            Path.Combine(workspace.RunsRoot, "alias.log"), workspace.LogPath("job")),
            "Synthetic hard link fixture could not be created.");
        var evidence = JobLiveActivityReader.Preflight(
            new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs, workspace.RunsRoot);
        Assert.IsFalse(evidence["job"].IsAvailable);
        Assert.IsNull(Read(workspace).Doing);
    }

    [TestMethod]
    public void WrappedPartTypes_AreObserved_AndUnsafePartIdsAreNotDisplayed()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", JsonSerializer.Serialize(new
        {
            type = "message-part", sessionID = "shared-session", timestamp = 1_800_000_000_000,
            role = "assistant",
            part = new { type = "text", id = "unsafe\u202e", messageID = "m", text = "wrapped text" }
        }), Finish("m"));
        Assert.AreEqual("wrapped text", Read(workspace).Doing?.Text);
        Assert.IsNull(Read(workspace).Doing?.PartId);
        Assert.AreEqual("wrapped text", Read(workspace).Done?.Text);
    }

    [TestMethod]
    public async Task V3Monitoring_RestartPreservesCostsAndDedup_ExcerptsNeverEnterLedgerOrLogs()
    {
        using var workspace = new TestWorkspace();
        const string secret = "synthetic-private-result-unique-092718";
        workspace.WriteMetadata("job", status: "DONE", directory: @"C:\one\same-name");
        workspace.WriteLog("job", Text(secret, "m"), Finish("m"));
        var database = Path.Combine(workspace.Root, "events-v3.db");
        async Task<MonitoringSnapshot> Scan()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            MonitoringSnapshot? result = null;
            var service = new ForgeMonitoringService(workspace.RunsRoot,
                new MonitoringRunLoggerFactory(workspace.LogsRoot), clock: () => Now,
                ledgerFactory: () => EventLedger.OpenV3Projection(database, workspace.Root));
            await service.RunAsync(snapshot =>
            {
                result = snapshot;
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);
            return result!;
        }
        var first = await Scan();
        Assert.AreEqual(secret, first.Jobs.Single().LiveActivity.Done?.Text);
        Assert.AreEqual(2, first.Jobs.Single().ParsedEventCount);
        Assert.AreEqual(0.25, first.Jobs.Single().ObservedCost);
        var reopened = await Scan();
        Assert.AreEqual(2, reopened.Jobs.Single().ParsedEventCount);
        Assert.AreEqual(0.25, reopened.Jobs.Single().ObservedCost);
        Assert.AreEqual(secret, reopened.Jobs.Single().LiveActivity.Done?.Text);
        var row = new JobRowViewModel(reopened.Jobs.Single());
        Assert.AreEqual(@"C:\one\same-name", row.ProjectText);
        StringAssert.Contains(row.TaskText, "N/A");
        StringAssert.Contains(row.DoneProvenance, "unverified");
        StringAssert.Contains(row.DoneProvenance, "message m");
        StringAssert.Contains(row.LiveActivityNotice, "never restored from SQLite");
        foreach (var file in Directory.GetFiles(workspace.LogsRoot, "*", SearchOption.AllDirectories))
            Assert.IsFalse(File.ReadAllText(file).Contains(secret, StringComparison.Ordinal), file);
        using (var ledger = EventLedger.OpenV3Projection(database, workspace.Root))
        {
            Assert.AreEqual(2L, ledger.GetJobAggregate("job").LogicalEventCount);
            Assert.IsFalse(ledger.ListTimelineEvents("job", 50).Any(item => item.Summary.Contains(secret)));
        }
        foreach (var file in Directory.GetFiles(workspace.Root, "events-v3.db*"))
            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(secret), file);

        // A fresh reader cannot reconstruct excerpts when the source is empty.
        File.WriteAllText(workspace.LogPath("job"), string.Empty);
        Assert.IsNull((await Scan()).Jobs.Single().LiveActivity.Done);
    }

    private static string RoleRecord(
        string type,
        string message,
        string? text,
        string[] rootRoles,
        string[] partRoles,
        string? partType = null)
    {
        var fields = new List<string>
        {
            $"\"id\":\"part-{message}\"",
            $"\"messageID\":\"{message}\"",
            $"\"type\":\"{partType ?? (text is null ? "step-finish" : "text")}\""
        };
        if (text is null)
        {
            fields.Add("\"reason\":\"stop\"");
            fields.Add("\"cost\":0.25");
        }
        else
        {
            fields.Add($"\"text\":{JsonSerializer.Serialize(text)}");
        }

        fields.AddRange(partRoles.Select(role => $"\"role\":{role}"));
        var rootFields = rootRoles.Select(role => $"\"role\":{role}").ToArray();
        var rootJson = rootFields.Length == 0 ? string.Empty : string.Join(",", rootFields) + ",";
        return $"{{\"type\":\"{type}\",\"sessionID\":\"shared-session\"," +
               $"\"timestamp\":1800000000000,{rootJson}\"part\":{{{string.Join(",", fields)}}}}}";
    }

    private static LiveActivitySnapshot Read(TestWorkspace workspace) =>
        Read(workspace, "job");

    private static LiveActivitySnapshot Read(
        TestWorkspace workspace,
        string jobId,
        long maxBytes = JobLiveActivityReader.MaxReadBytes,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(workspace, jobId);
        return JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            cancellationToken,
            maxBytes);
    }

    private static LiveActivitySnapshot ReadPrepared(
        TestWorkspace workspace,
        PreparedActivity prepared,
        long maxBytes = JobLiveActivityReader.MaxReadBytes,
        Action? beforeContentRead = null,
        Action? beforePublishCheck = null) =>
        JobLiveActivityReader.Read(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            workspace.RunsRoot,
            Now,
            CancellationToken.None,
            maxBytes,
            beforeContentRead,
            beforePublishCheck);

    private static PreparedActivity Prepare(
        TestWorkspace workspace,
        string jobId = "job",
        LedgerSourceIdentityReader? identityReader = null)
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == jobId);
        var result = Ingest(workspace, job, identityReader);
        Assert.IsTrue(result.IsSuccess, result.Status.ToString());
        Assert.IsNotNull(result.SourceObservation, "Successful ingestion must expose source evidence.");
        return new PreparedActivity(job, evidence[jobId], result.SourceObservation!);
    }

    private static LedgerIngestResult Ingest(
        TestWorkspace workspace,
        ForgeJobMetadata job,
        LedgerSourceIdentityReader? identityReader = null)
    {
        Assert.IsNotNull(job.LogPath);
        var database = Path.Combine(workspace.Root, "activity.db");
        using var ledger = EventLedger.OpenV3Projection(
            database, workspace.Root, identityReader: identityReader);
        return ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, Now);
    }

    private static string Text(string text, string? message, string? session = "shared-session",
        string? role = "assistant", long timestamp = 1_800_000_000_000) => JsonSerializer.Serialize(new
        {
            type = "text", sessionID = session, timestamp,
            part = new { id = "text-" + message, messageID = message, text, role }
        });

    private static string Finish(
        string message,
        string reason = "stop",
        string? role = "assistant") => JsonSerializer.Serialize(new
        {
            type = "step_finish", sessionID = "shared-session", timestamp = 1_800_000_001_000,
            part = new
            {
                id = "finish-" + message, messageID = message, type = "step-finish",
                reason, cost = 0.25, role
            }
        });

    private static string ToolUse(
        long timestamp,
        string tool,
        string status = "completed",
        long? start = null,
        long? end = null,
        string message = "msg_1") => JsonSerializer.Serialize(new
        {
            type = "tool_use", sessionID = "shared-session", timestamp, role = "assistant",
            part = new
            {
                id = "tool-" + message, partID = "tool-" + message, sessionID = "shared-session",
                messageID = message, type = "tool", tool,
                state = start is not null && end is not null
                    ? new { status, time = new { start, end } }
                    : (object)new { status }
            }
        });

    private sealed record PreparedActivity(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
