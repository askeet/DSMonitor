using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    public void RolelessToolActivity_IsObservedUnverified()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", ToolUse(
            1_800_000_001_000,
            "shell",
            "completed",
            1_800_000_000_000,
            1_800_000_001_000,
            role: null));
        var activity = Read(workspace);
        Assert.IsNotNull(activity.Doing);
        StringAssert.Contains(activity.Doing!.Text, "completed");
        StringAssert.Contains(activity.Doing.Kind, "Observed");
        StringAssert.Contains(activity.Doing.Kind, "unverified");
        Assert.IsFalse(activity.Doing.Kind.Contains("Assistant", StringComparison.Ordinal));
        Assert.IsNull(activity.Done);
    }

    [TestMethod]
    public void RolelessActivity_DoesNotBypassAmbiguousSourceOwnership()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteMetadata("other", logPath: workspace.LogPath("job"));
        workspace.WriteLog("job", Text("roleless but ambiguous", "m", role: null));
        Assert.IsNull(Read(workspace).Doing);
    }

    [TestMethod]
    public void RolelessSharedSessionJobs_KeepDistinctMessagesAndUnverifiedLabels()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0");
        workspace.WriteMetadata("job1");
        workspace.WriteLog("job0", Text("alpha roleless", "msg-0", role: null), Finish("msg-0", role: null));
        workspace.WriteLog("job1", Text("bravo roleless", "msg-1", role: null), Finish("msg-1", role: null));

        foreach (var jobId in new[] { "job0", "job1" })
        {
            var activity = Read(workspace, jobId);
            var expected = jobId == "job0" ? "alpha roleless" : "bravo roleless";
            var message = jobId == "job0" ? "msg-0" : "msg-1";
            Assert.AreEqual(expected, activity.Doing?.Text, jobId);
            Assert.AreEqual(expected, activity.Done?.Text, jobId);
            Assert.AreEqual(message, activity.Done?.MessageId, jobId);
            Assert.AreEqual("shared-session", activity.Done?.SessionId, jobId);
            Assert.AreEqual(jobId, activity.Done?.JobId, jobId);
            StringAssert.Contains(activity.Done!.Kind, "unverified");
            Assert.IsFalse(activity.Done.Kind.Contains("Assistant", StringComparison.Ordinal), jobId);
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
    public void RolelessSyntheticUserMessage_IsObservedOnlyAsUnverified()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job",
            Text("user prompt", "user-1", role: null),
            Finish("user-1", role: null));
        var activity = Read(workspace);
        Assert.AreEqual("user prompt", activity.Doing?.Text);
        Assert.AreEqual("user prompt", activity.Done?.Text);
        StringAssert.Contains(activity.Doing!.Kind, "Observed");
        StringAssert.Contains(activity.Doing.Kind, "unverified");
        StringAssert.Contains(activity.Done!.Kind, "Observed");
        StringAssert.Contains(activity.Done.Kind, "unverified");
        Assert.IsFalse(activity.Doing.Kind.Contains("Assistant", StringComparison.Ordinal));
        Assert.IsFalse(activity.Done.Kind.Contains("Agent-reported", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RoleEvidence_ClassifiesAssistantRolelessAndRejectsInvalid()
    {
        var negative = new (string Name, string[] RootRoles, string[] PartRoles)[]
        {
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

        var roleless = new (string Name, string[] RootRoles, string[] PartRoles)[]
        {
            ("absent", Array.Empty<string>(), Array.Empty<string>()),
            ("null-only", new[] { "null" }, new[] { "null" })
        };
        foreach (var scenario in roleless)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job",
                RoleRecord("text", "m", "payload", scenario.RootRoles, scenario.PartRoles),
                RoleRecord("step_finish", "m", null, scenario.RootRoles, scenario.PartRoles));
            var activity = Read(workspace);
            Assert.AreEqual("payload", activity.Doing?.Text, scenario.Name);
            Assert.AreEqual("payload", activity.Done?.Text, scenario.Name);
            StringAssert.Contains(activity.Doing!.Kind, "Observed");
            StringAssert.Contains(activity.Doing.Kind, "unverified");
            StringAssert.Contains(activity.Done!.Kind, "Observed");
            StringAssert.Contains(activity.Done.Kind, "unverified");
            Assert.IsFalse(activity.Doing.Kind.Contains("Assistant", StringComparison.Ordinal));
            Assert.IsFalse(activity.Done.Kind.Contains("Agent-reported", StringComparison.Ordinal));
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
            StringAssert.Contains(activity.Doing!.Kind, "Assistant", scenario.Name);
            StringAssert.Contains(activity.Done!.Kind, "unverified", scenario.Name);
            Assert.IsFalse(
                activity.Done.Kind.Contains("roleless", StringComparison.OrdinalIgnoreCase),
                scenario.Name);
        }
    }

    [TestMethod]
    public void MixedAssistantAndRolelessTextFinish_IsNeverLaundered()
    {
        var scenarios = new (string Name, string[] TextRoles, string[] FinishRoles)[]
        {
            (
                "assistant-text-roleless-finish",
                new[] { "\"assistant\"" },
                Array.Empty<string>()),
            (
                "roleless-text-assistant-finish",
                Array.Empty<string>(),
                new[] { "\"assistant\"" })
        };
        foreach (var scenario in scenarios)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job",
                RoleRecord("text", "m", "payload", scenario.TextRoles, Array.Empty<string>()),
                RoleRecord("step_finish", "m", null, scenario.FinishRoles, Array.Empty<string>()));
            var activity = Read(workspace);
            Assert.AreEqual("payload", activity.Doing?.Text, scenario.Name);
            Assert.AreEqual("payload", activity.Done?.Text, scenario.Name);
            StringAssert.Contains(activity.Done!.Kind, "Observed", scenario.Name);
            StringAssert.Contains(activity.Done.Kind, "unverified", scenario.Name);
            Assert.IsFalse(
                activity.Done.Kind.Contains("Agent-reported", StringComparison.Ordinal),
                scenario.Name);
        }
    }

    [TestMethod]
    public void RolelessFinish_StillRequiresMatchingMessageAndStop()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job",
            Text("candidate", "m", role: null),
            Finish("other", role: null),
            Finish("m", "tool-calls", role: null));
        var activity = Read(workspace);
        Assert.AreEqual("candidate", activity.Doing?.Text);
        Assert.IsNull(activity.Done);
        workspace.AppendLog("job", Finish("m", role: null));
        var completed = Read(workspace);
        Assert.AreEqual("candidate", completed.Done?.Text);
        StringAssert.Contains(completed.Done!.Kind, "unverified");
        Assert.IsFalse(completed.Done.Kind.Contains("Agent-reported", StringComparison.Ordinal));
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
        Assert.AreEqual("same-name", row.ProjectText);
        Assert.AreEqual(@"C:\one\same-name", row.ProjectTooltip);
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

    [TestMethod]
    public void FullReport_ReturnsCompleteOriginalTextBeyondPreviewAnd4278Characters()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var original = "line1\nline2\t😀\u202e" + new string('é', 5_000);
        workspace.WriteLog("job", Text(original, "m"));
        var prepared = Prepare(workspace);
        var preview = JobLiveActivityReader.Read(
            prepared.Job, prepared.Evidence, prepared.Observation, workspace.RunsRoot, Now);

        Assert.IsNotNull(preview.Doing);
        Assert.AreEqual(JobLiveActivityReader.MaxExcerptLength, preview.Doing!.Text.Length);
        Assert.IsFalse(preview.Doing.Text.Contains('\n'));
        Assert.IsFalse(preview.Doing.Text.Contains('\u202e'));

        var result = ReadFullReport(workspace, prepared, FullReportTarget.FromExcerpt(preview.Doing));
        Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(original, result.Text);
        Assert.AreEqual("job", result.JobId);
        Assert.AreEqual("shared-session", result.SessionId);
        Assert.AreEqual("m", result.MessageId);
        Assert.AreEqual("text-m", result.PartId);
        Assert.AreEqual(preview.Doing.ByteOffset, result.ByteOffset);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000), result.SourceTimestamp);
        Assert.AreEqual(Now, result.ObservedAt);
        Assert.IsTrue(result.RecordLengthBytes > 5_000);
        StringAssert.Contains(result.Kind, "Assistant");
        StringAssert.Contains(result.Provenance, "part text-m");
        StringAssert.Contains(result.Provenance, "source");
    }

    [TestMethod]
    public void ReportedStatus_IntegrationPreservesPreviewFullReportAndForgeStatus()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job", status: "DONE");
        const string report = "Plan complete.\nStatus: **v1.2.3-complete**\n";
        workspace.WriteLog("job", Text(report, "m"), Finish("m"));
        var prepared = Prepare(workspace);
        var forgeStatus = prepared.Job.ExplicitStatus;

        var preview = JobLiveActivityReader.Read(
            prepared.Job, prepared.Evidence, prepared.Observation, workspace.RunsRoot, Now);
        Assert.IsNotNull(preview.Doing);
        StringAssert.Contains(preview.Doing!.Text, "Status:");

        var full = ReadFullReport(
            workspace,
            prepared,
            Target("m", offset: 0, digest: FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Complete, full.Status, full.Reason);
        Assert.AreEqual(report, full.Text);

        var extraction = ReportedStatusParser.Extract(full);
        Assert.AreEqual(ReportedStatusState.Reported, extraction.State, extraction.Reason);
        Assert.AreEqual("v1.2.3-complete", extraction.Value);
        Assert.AreEqual(15, extraction.Value!.Length);
        Assert.IsTrue(extraction.IsRoleVerified);
        Assert.AreSame(full, extraction.Source);
        Assert.AreEqual("job", extraction.Source.JobId);
        Assert.AreEqual("shared-session", extraction.Source.SessionId);
        Assert.AreEqual("m", extraction.Source.MessageId);
        Assert.AreEqual("text-m", extraction.Source.PartId);
        StringAssert.Contains(extraction.Provenance, "part text-m");

        // Extraction is read-only: the prior preview, full report, and Forge
        // metadata status remain unchanged.
        Assert.AreEqual(report, full.Text);
        Assert.AreEqual(ForgeJobStatus.Done, forgeStatus);
        Assert.AreEqual(forgeStatus, prepared.Job.ExplicitStatus);
        var after = JobLiveActivityReader.Read(
            prepared.Job, prepared.Evidence, prepared.Observation, workspace.RunsRoot, Now);
        Assert.AreEqual(preview.Doing.Text, after.Doing?.Text);
    }

    [TestMethod]
    public void ReportedStatus_IntegrationRejectsUserRoleAndIsolatesSharedSessions()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0");
        workspace.WriteMetadata("job1");
        workspace.WriteMetadata("user");
        workspace.WriteLog("job0", Text("Status: DONE", "m0"));
        workspace.WriteLog("job1", Text("Status: RUNNING", "m1", role: null));
        workspace.WriteLog("user", Text("Status: FAILED", "mu", role: "user"));

        var prepared0 = Prepare(workspace, "job0");
        var prepared1 = Prepare(workspace, "job1");
        var preparedUser = Prepare(workspace, "user");

        var full0 = ReadFullReport(
            workspace,
            prepared0,
            new FullReportTarget(
                "job0", "shared-session", "m0", "text-m0", 0, FirstRecordDigest(workspace, "job0")));
        var full1 = ReadFullReport(
            workspace,
            prepared1,
            new FullReportTarget(
                "job1", "shared-session", "m1", "text-m1", 0, FirstRecordDigest(workspace, "job1")));
        var fullUser = ReadFullReport(
            workspace,
            preparedUser,
            new FullReportTarget(
                "user", "shared-session", "mu", "text-mu", 0, FirstRecordDigest(workspace, "user")));

        Assert.AreEqual(FullReportStatus.Complete, full0.Status, full0.Reason);
        Assert.AreEqual(FullReportStatus.Complete, full1.Status, full1.Reason);
        Assert.AreEqual(FullReportStatus.Unsafe, fullUser.Status, fullUser.Reason);

        var extraction0 = ReportedStatusParser.Extract(full0);
        var extraction1 = ReportedStatusParser.Extract(full1);
        var userExtraction = ReportedStatusParser.Extract(fullUser);

        Assert.AreEqual(ReportedStatusState.Reported, extraction0.State, extraction0.Reason);
        Assert.AreEqual("DONE", extraction0.Value);
        Assert.AreEqual("job0", extraction0.Source.JobId);
        Assert.AreEqual("shared-session", extraction0.Source.SessionId);
        Assert.AreEqual("m0", extraction0.Source.MessageId);
        Assert.AreEqual("text-m0", extraction0.Source.PartId);
        Assert.IsTrue(extraction0.IsRoleVerified);

        Assert.AreEqual(ReportedStatusState.Reported, extraction1.State, extraction1.Reason);
        Assert.AreEqual("RUNNING", extraction1.Value);
        Assert.AreEqual("job1", extraction1.Source.JobId);
        Assert.AreEqual("shared-session", extraction1.Source.SessionId);
        Assert.AreEqual("m1", extraction1.Source.MessageId);
        Assert.AreEqual("text-m1", extraction1.Source.PartId);
        Assert.IsTrue(extraction1.IsUnverified);
        StringAssert.Contains(extraction1.Provenance, "unverified");

        Assert.AreEqual(ReportedStatusState.Ineligible, userExtraction.State, userExtraction.Reason);
        Assert.IsNull(userExtraction.Value);
        Assert.AreEqual(0, userExtraction.Declarations.Count);
    }

    [TestMethod]
    public async Task ReportedStatus_V3MonitoringBindsInitialRefreshIsolationAndUnknownStatus()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0");
        workspace.WriteMetadata("job1");
        workspace.WriteLog("job0", Text("Status: DONE", "m0"), Finish("m0"));
        workspace.WriteLog("job1", Text("Status: RUNNING", "m1"), Finish("m1"));
        var database = Path.Combine(workspace.Root, "events-v3.db");

        async Task<MonitoringSnapshot> Scan()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            MonitoringSnapshot? result = null;
            var service = new ForgeMonitoringService(
                workspace.RunsRoot,
                new MonitoringRunLoggerFactory(workspace.LogsRoot),
                clock: () => Now,
                ledgerFactory: () => EventLedger.OpenV3Projection(database, workspace.Root));
            await service.RunAsync(snapshot =>
            {
                result = snapshot;
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);
            return result!;
        }

        var initial = await Scan();
        var initial0 = initial.Jobs.Single(job => job.JobId == "job0");
        var initial1 = initial.Jobs.Single(job => job.JobId == "job1");
        Assert.AreEqual(ForgeJobStatus.Unknown, initial0.Status);
        Assert.AreEqual(ForgeJobStatus.Unknown, initial0.Metadata.ExplicitStatus);
        Assert.AreEqual(
            ReportedStatusState.Reported,
            initial0.ReportedStatus?.State,
            initial0.ReportedStatus?.Reason);
        Assert.AreEqual("DONE", initial0.ReportedStatus!.Value);
        Assert.AreEqual("job0", initial0.ReportedStatus.Source.JobId);
        Assert.AreEqual("shared-session", initial0.ReportedStatus.Source.SessionId);
        Assert.AreEqual("m0", initial0.ReportedStatus.Source.MessageId);
        Assert.AreEqual("text-m0", initial0.ReportedStatus.Source.PartId);

        Assert.AreEqual("RUNNING", initial1.ReportedStatus?.Value);
        Assert.AreEqual("job1", initial1.ReportedStatus!.Source.JobId);
        Assert.AreEqual("shared-session", initial1.ReportedStatus.Source.SessionId);
        Assert.AreEqual("m1", initial1.ReportedStatus.Source.MessageId);
        Assert.AreEqual("text-m1", initial1.ReportedStatus.Source.PartId);

        var initialRow0 = new JobRowViewModel(initial0);
        var initialRow1 = new JobRowViewModel(initial1);
        Assert.AreEqual("DONE", initialRow0.ReportedStatusText);
        Assert.AreEqual("RUNNING", initialRow1.ReportedStatusText);
        StringAssert.Contains(initialRow0.ReportedStatusTooltip, "unverified agent-reported");

        // Refresh only job0; job1 must keep its own message-bound value.
        workspace.AppendLog(
            "job0",
            Text("Status: **FAILED**", "m0-refresh", timestamp: 1_800_000_000_500),
            Finish("m0-refresh"));
        var refreshed = await Scan();
        var refreshed0 = refreshed.Jobs.Single(job => job.JobId == "job0");
        var refreshed1 = refreshed.Jobs.Single(job => job.JobId == "job1");
        Assert.AreEqual("FAILED", refreshed0.ReportedStatus?.Value);
        Assert.AreEqual("m0-refresh", refreshed0.ReportedStatus!.Source.MessageId);
        Assert.AreEqual("RUNNING", refreshed1.ReportedStatus?.Value);
        Assert.AreEqual(ForgeJobStatus.Unknown, refreshed0.Status);
        Assert.AreEqual(ForgeJobStatus.Unknown, refreshed0.Metadata.ExplicitStatus);

        initialRow0.Update(refreshed0, refreshed.ObservedAt);
        Assert.AreEqual("FAILED", initialRow0.ReportedStatusText);
        StringAssert.StartsWith(initialRow0.ReportedStatusTooltip, "FAILED");
        Assert.AreEqual("RUNNING", initialRow1.ReportedStatusText);
    }

    [TestMethod]
    public async Task ReportedStatus_SingleRunningService_RefreshesNewerTextBeforeFinishAndAfterTool()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0");
        workspace.WriteMetadata("job1");
        workspace.WriteLog("job0", Text("Status: DONE", "m0"), Finish("m0"));
        workspace.WriteLog("job1", Text("Status: RUNNING", "m1"), Finish("m1"));
        var database = Path.Combine(workspace.Root, "events-v3.db");
        var clockTicks = 0;
        DateTimeOffset Clock() => Now.AddMilliseconds(Interlocked.Increment(ref clockTicks));

        await using var harness = new RunningServiceHarness();
        harness.Start(new ForgeMonitoringService(
            workspace.RunsRoot,
            new MonitoringRunLoggerFactory(workspace.LogsRoot),
            new MonitoringOptions(TimeSpan.FromMilliseconds(50)),
            Clock,
            () => EventLedger.OpenV3Projection(database, workspace.Root)));

        var initial = await harness.WaitForAsync(
            snapshot => Job(snapshot, "job0")?.ReportedStatus?.Value == "DONE",
            TimeSpan.FromSeconds(10));
        var initial0 = Job(initial, "job0")!;
        Assert.AreEqual("m0", initial0.ReportedStatus!.Source.MessageId);
        Assert.AreEqual(initial.ObservedAt, initial0.ReportedStatus.Source.ObservedAt);

        // A cached extraction must be revalidated with the current timestamp.
        var unchanged = await harness.WaitForAsync(
            snapshot => snapshot.ObservedAt > initial.ObservedAt &&
                        Job(snapshot, "job0")?.ReportedStatus?.Value == "DONE",
            TimeSpan.FromSeconds(10));
        var unchanged0 = Job(unchanged, "job0")!;
        Assert.AreEqual(unchanged.ObservedAt, unchanged0.ReportedStatus!.Source.ObservedAt);
        Assert.AreEqual("m0", unchanged0.ReportedStatus.Source.MessageId);

        // Newer eligible text without a new finish must supersede the old Done.
        workspace.AppendLog(
            "job0",
            Text("Status: FAILED", "m0-new", timestamp: 1_800_000_000_500));
        var beforeFinish = await harness.WaitForAsync(
            snapshot => Job(snapshot, "job0")?.ReportedStatus?.Value == "FAILED",
            TimeSpan.FromSeconds(10));
        var beforeFinish0 = Job(beforeFinish, "job0")!;
        var beforeFinish1 = Job(beforeFinish, "job1")!;
        Assert.AreEqual("m0-new", beforeFinish0.ReportedStatus!.Source.MessageId);
        Assert.AreEqual("text-m0-new", beforeFinish0.ReportedStatus.Source.PartId);
        Assert.AreEqual("RUNNING", beforeFinish1.ReportedStatus?.Value);
        Assert.AreEqual("m1", beforeFinish1.ReportedStatus!.Source.MessageId);
        Assert.AreEqual(ForgeJobStatus.Unknown, beforeFinish0.Status);

        // A later tool event must not revert the reported status to the old Done.
        workspace.AppendLog(
            "job0",
            ToolUse(
                1_800_000_000_600,
                "shell",
                "completed",
                1_800_000_000_600,
                1_800_000_000_700,
                message: "m0-new"));
        var afterTool = await harness.WaitForAsync(
            snapshot => Job(snapshot, "job0")?.LiveActivity.Doing?.Kind
                .Contains("tool", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(10));
        var afterTool0 = Job(afterTool, "job0")!;
        Assert.AreEqual("FAILED", afterTool0.ReportedStatus?.Value);
        Assert.AreEqual("m0-new", afterTool0.ReportedStatus!.Source.MessageId);
        Assert.AreEqual("RUNNING", Job(afterTool, "job1")?.ReportedStatus?.Value);
        Assert.AreEqual(ForgeJobStatus.Unknown, afterTool0.Status);
        Assert.AreEqual(ForgeJobStatus.Unknown, afterTool0.Metadata.ExplicitStatus);
        await harness.StopAsync();
    }

    [TestMethod]
    public void ReportedStatus_CacheKeyBindsPhysicalIdentityGenerationAndRecord()
    {
        var identityA = new LedgerSourceIdentity(0x1234, 0x5678, 0x9ABC);
        var identityB = new LedgerSourceIdentity(0x1234, 0x5678, 0x9ABD);
        var evidence = new LiveActivitySourceEvidence(
            "job",
            @"C:\synthetic\runs\job.log",
            @"C:\synthetic\runs\job.log",
            identityA,
            Array.Empty<string>());
        var observation = new LedgerSourceObservation(
            "job",
            @"C:\synthetic\runs\job.log",
            identityA,
            identityA,
            7,
            100,
            100,
            false);
        var target = new FullReportTarget(
            "job",
            "session",
            "message",
            "part",
            0,
            new string('a', 64));

        Assert.IsTrue(ForgeMonitoringService.CanReuseReportedStatus(evidence, observation));
        var key = ForgeMonitoringService.CreateReportedStatusCacheKey(
            evidence,
            observation,
            target);
        Assert.AreEqual(
            key,
            ForgeMonitoringService.CreateReportedStatusCacheKey(evidence, observation, target));

        var replacementObservation = observation with { PhysicalIdentity = identityB };
        Assert.IsFalse(
            ForgeMonitoringService.CanReuseReportedStatus(evidence, replacementObservation));
        Assert.AreNotEqual(
            key,
            ForgeMonitoringService.CreateReportedStatusCacheKey(
                evidence,
                replacementObservation,
                target));

        var newGeneration = observation with { GenerationId = 8 };
        Assert.IsTrue(ForgeMonitoringService.CanReuseReportedStatus(evidence, newGeneration));
        Assert.AreNotEqual(
            key,
            ForgeMonitoringService.CreateReportedStatusCacheKey(
                evidence,
                newGeneration,
                target));

        var differentRecord = target with { ExpectedRecordDigest = new string('b', 64) };
        Assert.AreNotEqual(
            key,
            ForgeMonitoringService.CreateReportedStatusCacheKey(
                evidence,
                observation,
                differentRecord));
    }

    private static ForgeJobSnapshot? Job(MonitoringSnapshot snapshot, string jobId) =>
        snapshot.Jobs.SingleOrDefault(job => job.JobId == jobId);

    [TestMethod]
    public void FullReport_RetrievesRecordBeyond64KiBPreviewWithoutChangingPreviewBehavior()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var original = new string('x', 80_000);
        workspace.WriteLog("job", Text(original, "m"), Finish("m"));

        // The 64 KiB preview still starts inside the oversized text record and
        // therefore shows no attributed excerpt.
        var preview = Read(workspace);
        Assert.IsNull(preview.Doing);
        Assert.IsNull(preview.Done);

        var result = ReadFullReport(
            workspace,
            Target("m", digest: FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
        Assert.AreEqual(original, result.Text);
    }

    [TestMethod]
    public void FullReport_256KiBBoundary_ExactFitIsComplete_OverCapIsIncomplete()
    {
        var baseLength = Encoding.UTF8.GetByteCount(Text(string.Empty, "m"));
        var fittingText = new string('y', JobLiveActivityReader.MaxFullReportBytes - 1 - baseLength);
        using (var workspace = new TestWorkspace())
        {
            workspace.WriteMetadata("job");
            var record = Text(fittingText, "m");
            Assert.AreEqual(
                JobLiveActivityReader.MaxFullReportBytes - 1,
                Encoding.UTF8.GetByteCount(record));
            File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));

            var result = ReadFullReport(
                workspace,
                Target("m", offset: 0, digest: FirstRecordDigest(workspace)));
            Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
            Assert.AreEqual(fittingText, result.Text);
            Assert.AreEqual(0L, result.ByteOffset);
            Assert.AreEqual(
                (long)(JobLiveActivityReader.MaxFullReportBytes - 1),
                result.RecordLengthBytes);
        }

        var overText = new string('y', JobLiveActivityReader.MaxFullReportBytes + 1 - baseLength);
        using (var workspace = new TestWorkspace())
        {
            workspace.WriteMetadata("job");
            var record = Text(overText, "m");
            Assert.AreEqual(
                JobLiveActivityReader.MaxFullReportBytes + 1,
                Encoding.UTF8.GetByteCount(record));
            File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));

            var result = ReadFullReport(workspace, Target("m", offset: 0));
            Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
            Assert.IsNull(result.Text);
        }
    }

    [TestMethod]
    public void FullReport_MalformedTargetRecord_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(
                "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"text\":\"unterminated\"\n"));

        var result = ReadFullReport(workspace, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_InvalidUtf8TargetRecord_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            new byte[] { (byte)'{', 0xFF, (byte)'}', (byte)'\n' });

        var result = ReadFullReport(workspace, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_MissingTerminatingNewline_IsIncomplete()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(Text("no newline", "m")));
        var prepared = Prepare(workspace);

        var byOffset = ReadFullReport(workspace, prepared, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Incomplete, byOffset.Status, byOffset.Reason);
        StringAssert.Contains(byOffset.Reason, "newline");
        Assert.IsNull(byOffset.Text);

        var byIdentity = ReadFullReport(workspace, prepared, Target("m"));
        Assert.AreEqual(FullReportStatus.Incomplete, byIdentity.Status, byIdentity.Reason);
        StringAssert.Contains(byIdentity.Reason, "newline");
        Assert.IsNull(byIdentity.Text);
    }

    [TestMethod]
    public void FullReport_MissingSource_IsUnavailable()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("present", "m"));
        var prepared = Prepare(workspace);
        File.Delete(workspace.LogPath("job"));

        var result = ReadFullReport(workspace, prepared, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unavailable, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_TruncatedSource_IsIncomplete()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("before truncate", "m"));
        var prepared = Prepare(workspace);
        using (var stream = new FileStream(
                   workspace.LogPath("job"),
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(0);
        }

        var result = ReadFullReport(workspace, prepared, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_ReplacementDuringRead_DiscardsTheReport()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("before replacement", "m"));
        var prepared = Prepare(workspace);
        var digest = FirstRecordDigest(workspace);
        var replacement = Path.Combine(workspace.RunsRoot, "full-report-during-read.tmp");

        var result = ReadFullReport(
            workspace,
            prepared,
            Target("m", offset: 0, digest: digest),
            beforePublishCheck: () =>
            {
                File.WriteAllText(replacement, Text("after replacement", "m") + "\n");
                File.Delete(workspace.LogPath("job"));
                File.Move(replacement, workspace.LogPath("job"));
            });
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_SameSizeDifferentPartReplacement_NeverSubstitutesAnotherPart()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var first = Text("alpha", "m");
        var second = Text("alpha", "n");
        Assert.AreEqual(first.Length, second.Length);
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(first + "\n"));
        var prepared = Prepare(workspace);
        var digest = FirstRecordDigest(workspace);
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(second + "\n"));

        var result = ReadFullReport(
            workspace,
            prepared,
            Target("m", offset: 0, digest: digest));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_HardLinkedSource_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("aliased content", "m"));
        Assert.IsTrue(
            ReparsePointFixture.TryCreateHardLink(
                Path.Combine(workspace.RunsRoot, "full-report-alias.log"),
                workspace.LogPath("job")),
            "Synthetic hard link fixture could not be created.");

        var result = ReadFullReport(workspace, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_AmbiguousOwnership_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteMetadata("other", logPath: workspace.LogPath("job"));
        workspace.WriteLog("job", Text("private", "m"));

        var result = ReadFullReport(workspace, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_SourceIdentityMismatch_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("identity", "m"));
        var prepared = Prepare(workspace, identityReader: _ => SyntheticIdentityB);
        Assert.IsFalse(prepared.Observation.IsConsistent);

        var result = ReadFullReport(workspace, prepared, Target("m", offset: 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_Roles_RejectUserAndConflicts_KeepRolelessUnverified()
    {
        var scenarios = new (string Name, string Record, FullReportStatus Expected)[]
        {
            (
                "user",
                RoleRecord("text", "m", "payload", new[] { "\"user\"" }, Array.Empty<string>()),
                FullReportStatus.Unsafe),
            (
                "conflict",
                RoleRecord(
                    "text",
                    "m",
                    "payload",
                    new[] { "\"assistant\"" },
                    new[] { "\"user\"" }),
                FullReportStatus.Unsafe),
            (
                "roleless",
                RoleRecord("text", "m", "payload", Array.Empty<string>(), Array.Empty<string>()),
                FullReportStatus.Complete)
        };

        foreach (var scenario in scenarios)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            workspace.WriteLog("job", scenario.Record);
            var result = ReadFullReport(
                workspace,
                TargetPart("m", "part-m", offset: 0, digest: FirstRecordDigest(workspace)));
            Assert.AreEqual(
                scenario.Expected,
                result.Status,
                scenario.Name + ": " + result.Reason);
            if (scenario.Expected == FullReportStatus.Complete)
            {
                Assert.AreEqual("payload", result.Text, scenario.Name);
                StringAssert.Contains(result.Kind, "unverified", scenario.Name);
                Assert.IsFalse(
                    result.Kind.Contains("Assistant", StringComparison.Ordinal),
                    scenario.Name);
            }
            else
            {
                Assert.IsNull(result.Text, scenario.Name);
            }
        }
    }

    [TestMethod]
    public void FullReport_NonTextSelectedPart_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", ToolUse(
            1_800_000_001_000,
            "shell",
            "completed",
            1_800_000_000_000,
            1_800_000_001_000));
        var preview = Read(workspace);
        Assert.IsNotNull(preview.Doing);

        var result = ReadFullReport(workspace, FullReportTarget.FromExcerpt(preview.Doing!));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_SharedSession_IsolatesJobsAndNeverSubstitutesAnotherPart()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job0");
        workspace.WriteMetadata("job1");
        workspace.WriteLog("job0", Text("alpha", "m0"));
        workspace.WriteLog("job1", Text("bravo", "m1"));
        var prepared0 = Prepare(workspace, "job0");
        var prepared1 = Prepare(workspace, "job1");

        var own = ReadFullReport(
            workspace,
            prepared1,
            new FullReportTarget(
                "job1",
                "shared-session",
                "m1",
                "text-m1",
                0,
                FirstRecordDigest(workspace, "job1")));
        Assert.AreEqual(FullReportStatus.Complete, own.Status, own.Reason);
        Assert.AreEqual("bravo", own.Text);

        var cross = ReadFullReport(
            workspace,
            prepared0,
            new FullReportTarget("job0", "shared-session", "m1", "text-m1"));
        Assert.AreEqual(FullReportStatus.Unavailable, cross.Status, cross.Reason);
        Assert.IsNull(cross.Text);
    }

    [TestMethod]
    public void FullReport_TargetForDifferentJob_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("content", "m"));
        var prepared = Prepare(workspace);

        var result = ReadFullReport(
            workspace,
            prepared,
            new FullReportTarget("other", "shared-session", "m", "text-m", 0));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_MissingPartIdentity_IsUnavailableAndDoesNotGuess()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("content", "m"));

        var result = ReadFullReport(
            workspace,
            new FullReportTarget("job", "shared-session", "m", null));
        Assert.AreEqual(FullReportStatus.Unavailable, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_RepeatedReads_AreStableAndStateless()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text(new string('r', 5_000), "m"));
        var prepared = Prepare(workspace);
        var target = Target("m", offset: 0, digest: FirstRecordDigest(workspace));

        var first = ReadFullReport(workspace, prepared, target);
        var second = ReadFullReport(workspace, prepared, target);
        var third = ReadFullReport(workspace, prepared, target);
        foreach (var result in new[] { first, second, third })
        {
            Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
            Assert.AreEqual(5_000, result.Text?.Length);
            Assert.AreEqual(first.ByteOffset, result.ByteOffset);
            Assert.AreEqual(first.RecordLengthBytes, result.RecordLengthBytes);
            Assert.AreEqual(first.SourceTimestamp, result.SourceTimestamp);
        }
    }

    [TestMethod]
    public void FullReport_InvalidEscapedSurrogateText_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var record =
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"timestamp\":1800000000000," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"text\":\"bad \\uD800\"}}";
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));

        var result = ReadFullReport(
            workspace,
            Target("m", offset: 0, digest: FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_InvalidEscapedSurrogateIdentity_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var record =
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"bad \\uD800\",\"messageID\":\"m\",\"text\":\"payload\"}}";
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));

        var result = ReadFullReport(
            workspace,
            new FullReportTarget(
                "job",
                "shared-session",
                "m",
                "bad \uD800",
                0,
                FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void Preview_InvalidEscapedSurrogateRecord_IsSkippedAndLaterRecordShown()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var invalid =
            "{\"type\":\"text\",\"sessionID\":\"bad \\uD800\"," +
            "\"part\":{\"id\":\"text-bad\",\"messageID\":\"bad\",\"text\":\"bad \\uD800\"}}";
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(invalid + "\n" + Text("survives", "m") + "\n"));

        var activity = Read(workspace);
        Assert.AreEqual("survives", activity.Doing?.Text);
    }

    [TestMethod]
    public void FullReport_WindowStartInsideUnprovenRecord_IsIncomplete()
    {
        var baseLength = Encoding.UTF8.GetByteCount(Text(string.Empty, "m"));
        var body = new string('y', JobLiveActivityReader.MaxFullReportBytes - 1 - baseLength);
        var record = Text(body, "m");
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var prefix = new string('A', 100);
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(prefix + record + "\n"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record)));

        // File length is 100 + 262143 + 1, so the bounded window starts exactly
        // at the record offset with no preceding newline.
        var result = ReadFullReport(
            workspace,
            new FullReportTarget("job", "shared-session", "m", "text-m", 100, digest));
        Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
        StringAssert.Contains(result.Reason, "source-byte cap");
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_WindowStartRecord_CannotProvePredecessorWithinCap_IsIncomplete()
    {
        var baseLength = Encoding.UTF8.GetByteCount(Text(string.Empty, "m"));
        var body = new string('y', JobLiveActivityReader.MaxFullReportBytes - 1 - baseLength);
        var record = Text(body, "m");
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var prefix = new string('A', 99) + "\n";
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(prefix + record + "\n"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record)));

        // A real newline precedes the record at absolute byte 99, but proving it
        // would require a 262145th source byte, so the strict cap fails closed.
        var result = ReadFullReport(
            workspace,
            new FullReportTarget("job", "shared-session", "m", "text-m", 100, digest));
        Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
        StringAssert.Contains(result.Reason, "source-byte cap");
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_TargetInsideWindowWithPredecessorNewline_IsComplete()
    {
        var baseLength = Encoding.UTF8.GetByteCount(Text(string.Empty, "m"));
        var body = new string('y', JobLiveActivityReader.MaxFullReportBytes - 2 - baseLength);
        var record = Text(body, "m");
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var prefix = new string('A', 99) + "\n";
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(prefix + record + "\n"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record)));

        // The record starts one byte inside the window, so its predecessor
        // newline is available without exceeding the 262144-byte bound.
        var result = ReadFullReport(
            workspace,
            new FullReportTarget("job", "shared-session", "m", "text-m", 100, digest));
        Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
        Assert.AreEqual(body, result.Text);
    }

    [TestMethod]
    public void FullReport_UnterminatedDuplicateTargetTail_IsIncomplete()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var first = Text("first", "m");
        var duplicateTail = Text("second", "m");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(first + "\n" + duplicateTail));

        var result = ReadFullReport(
            workspace,
            Target("m", digest: FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
        StringAssert.Contains(result.Reason, "unterminated");
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_UnterminatedDifferentTail_StillReturnsCompleteMatch()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        var first = Text("first", "m");
        var otherTail = Text("tail", "other");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(first + "\n" + otherTail));

        var result = ReadFullReport(
            workspace,
            Target("m", digest: FirstRecordDigest(workspace)));
        Assert.AreEqual(FullReportStatus.Complete, result.Status, result.Reason);
        Assert.AreEqual("first", result.Text);
    }

    [TestMethod]
    public void FullReport_ConflictingOrDuplicateIdentityAliases_AreUnsafe()
    {
        var records = new (string Name, string Record)[]
        {
            (
                "session-alias",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionId\":\"other\"," +
                "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"text\":\"payload\"}}"),
            (
                "session-duplicate",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"text\":\"payload\"}}"),
            (
                "message-alias",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"messageId\":\"other\",\"text\":\"payload\"}}"),
            (
                "part-alias",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"text-m\",\"partID\":\"text-other\",\"messageID\":\"m\",\"text\":\"payload\"}}")
        };

        foreach (var scenario in records)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            File.WriteAllBytes(
                workspace.LogPath("job"),
                Encoding.UTF8.GetBytes(scenario.Record + "\n"));
            var result = ReadFullReport(
                workspace,
                Target("m", offset: 0, digest: FirstRecordDigest(workspace)));
            Assert.AreEqual(
                FullReportStatus.Unsafe,
                result.Status,
                scenario.Name + ": " + result.Reason);
            Assert.IsNull(result.Text, scenario.Name);
        }
    }

    [TestMethod]
    public void FullReport_SingleIdentityAliases_AreAccepted()
    {
        var records = new (string Name, string Record)[]
        {
            (
                "camel",
                "{\"type\":\"text\",\"part\":{\"id\":\"text-m\"," +
                "\"sessionId\":\"shared-session\",\"messageId\":\"m\",\"text\":\"payload\"}}"),
            (
                "partID",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"partID\":\"text-m\",\"messageID\":\"m\",\"text\":\"payload\"}}"),
            (
                "root-and-part-session",
                "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"text-m\",\"sessionID\":\"shared-session\"," +
                "\"messageID\":\"m\",\"text\":\"payload\"}}")
        };

        foreach (var scenario in records)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            File.WriteAllBytes(
                workspace.LogPath("job"),
                Encoding.UTF8.GetBytes(scenario.Record + "\n"));
            var result = ReadFullReport(
                workspace,
                Target("m", offset: 0, digest: FirstRecordDigest(workspace)));
            Assert.AreEqual(
                FullReportStatus.Complete,
                result.Status,
                scenario.Name + ": " + result.Reason);
            Assert.AreEqual("payload", result.Text, scenario.Name);
        }
    }

    [TestMethod]
    public void FullReport_TargetWithoutRecordBinding_IsIncomplete()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("content", "m"));
        var prepared = Prepare(workspace);

        var result = ReadFullReport(
            workspace,
            prepared,
            new FullReportTarget("job", "shared-session", "m", "text-m", 0));
        Assert.AreEqual(FullReportStatus.Incomplete, result.Status, result.Reason);
        StringAssert.Contains(result.Reason, "binding");
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_DigestMismatchAfterLengthChangingRewrite_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog("job", Text("alpha", "m"));
        var preview = Read(workspace);
        Assert.AreEqual("alpha", preview.Doing?.Text);
        var target = FullReportTarget.FromExcerpt(preview.Doing!);

        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(Text("bravo-longer", "m") + "\n"));
        var fresh = Prepare(workspace);

        var result = ReadFullReport(workspace, fresh, target);
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void FullReport_DigestMismatchAfterSameLengthRewrite_IsUnsafe()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(Text("alpha", "m") + "\n"));
        var preview = Read(workspace);
        Assert.AreEqual("alpha", preview.Doing?.Text);
        var target = FullReportTarget.FromExcerpt(preview.Doing!);

        // Same record length, same part identity, same physical file: only the
        // digest can prove the content did not change.
        File.WriteAllBytes(
            workspace.LogPath("job"),
            Encoding.UTF8.GetBytes(Text("bravo", "m") + "\n"));
        var fresh = Prepare(workspace);

        var result = ReadFullReport(workspace, fresh, target);
        Assert.AreEqual(FullReportStatus.Unsafe, result.Status, result.Reason);
        Assert.IsNull(result.Text);
    }

    [TestMethod]
    public void Preview_DuplicatePart_IsRejectedRegardlessOfOrder()
    {
        var records = new[]
        {
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"p-user\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"user secret\",\"role\":\"user\"}," +
            "\"part\":{\"id\":\"p-assistant\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"assistant visible\",\"role\":\"assistant\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"p-assistant\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"assistant visible\",\"role\":\"assistant\"}," +
            "\"part\":{\"id\":\"p-user\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"user secret\",\"role\":\"user\"}}"
        };
        foreach (var record in records)
        {
            AssertPreviewRejected(record);
        }
    }

    [TestMethod]
    public void FullReport_DuplicatePart_IsUnsafe()
    {
        var record =
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"p-user\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"user secret\",\"role\":\"user\"}," +
            "\"part\":{\"id\":\"p-assistant\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"assistant visible\",\"role\":\"assistant\"}}";
        AssertFullReport(
            record, "shared-session", "m", "p-assistant", 0, FullReportStatus.Unsafe);
    }

    [TestMethod]
    public void Preview_DuplicateText_IsRejected()
    {
        AssertPreviewRejected(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"first text\",\"text\":\"second text\",\"role\":\"assistant\"}}");
    }

    [TestMethod]
    public void FullReport_DuplicateText_IsUnsafe()
    {
        AssertFullReport(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"first text\",\"text\":\"second text\",\"role\":\"assistant\"}}",
            "shared-session", "m", "text-m", 0, FullReportStatus.Unsafe);
    }

    [TestMethod]
    public void Preview_DuplicateReason_DoesNotInferDone()
    {
        var scenarios = new (string Name, string Reason, bool ExpectDone)[]
        {
            ("duplicate", "\"reason\":\"tool-calls\",\"reason\":\"stop\"", false),
            ("single-stop", "\"reason\":\"stop\"", true),
            ("single-tool-calls", "\"reason\":\"tool-calls\"", false)
        };
        foreach (var scenario in scenarios)
        {
            using var workspace = new TestWorkspace();
            workspace.WriteMetadata("job");
            var finish =
                "{\"type\":\"step_finish\",\"sessionID\":\"shared-session\"," +
                "\"part\":{\"id\":\"finish-m\",\"messageID\":\"m\",\"type\":\"step-finish\"," +
                scenario.Reason + ",\"cost\":0.25,\"role\":\"assistant\"}}";
            File.WriteAllBytes(
                workspace.LogPath("job"),
                Encoding.UTF8.GetBytes(Text("candidate", "m") + "\n" + finish + "\n"));
            var activity = Read(workspace);
            Assert.AreEqual("candidate", activity.Doing?.Text, scenario.Name);
            if (scenario.ExpectDone)
            {
                Assert.AreEqual("candidate", activity.Done?.Text, scenario.Name);
            }
            else
            {
                Assert.IsNull(activity.Done, scenario.Name);
            }
        }
    }

    [TestMethod]
    public void Preview_DuplicateRoleAndType_AreRejected()
    {
        AssertPreviewRejected(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"payload\",\"role\":\"assistant\",\"role\":\"user\"}}");
        AssertPreviewRejected(
            "{\"type\":\"text\",\"type\":\"tool_use\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}");
        AssertPreviewRejected(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"type\":\"tool\"," +
            "\"text\":\"payload\"}}");
    }

    [TestMethod]
    public void FullReport_DuplicateRoleAndType_AreUnsafe()
    {
        AssertFullReport(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\"," +
            "\"text\":\"payload\",\"role\":\"assistant\",\"role\":\"user\"}}",
            "shared-session", "m", "text-m", 0, FullReportStatus.Unsafe);
        AssertFullReport(
            "{\"type\":\"text\",\"type\":\"tool_use\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}",
            "shared-session", "m", "text-m", 0, FullReportStatus.Unsafe);
        AssertFullReport(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"type\":\"tool\"," +
            "\"text\":\"payload\"}}",
            "shared-session", "m", "text-m", 0, FullReportStatus.Unsafe);
    }

    [TestMethod]
    public void Preview_ConflictingOrDuplicateAliases_AreRejected()
    {
        var records = new[]
        {
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionId\":\"other\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"messageId\":\"other\"," +
            "\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"partID\":\"text-other\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\"}}"
        };
        foreach (var record in records)
        {
            AssertPreviewRejected(record);
        }
    }

    [TestMethod]
    public void Preview_EqualConsistentAliases_AreAccepted()
    {
        var records = new[]
        {
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"partID\":\"text-m\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionId\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"sessionID\":\"shared-session\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"messageID\":\"m\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}"
        };
        foreach (var record in records)
        {
            AssertPreviewAccepted(record, "payload");
        }
    }

    [TestMethod]
    public void FullReport_EqualConsistentAliases_AreComplete()
    {
        var records = new[]
        {
            "{\"type\":\"text\",\"sessionID\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"partID\":\"text-m\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"sessionId\":\"shared-session\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}",
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"messageID\":\"m\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"m\",\"type\":\"text\",\"text\":\"payload\"}}"
        };
        foreach (var record in records)
        {
            AssertFullReport(
                record, "shared-session", "m", "text-m", 0,
                FullReportStatus.Complete, "payload");
        }
    }

    [TestMethod]
    public void Preview_RootPartMessageConflict_IsRejected()
    {
        AssertPreviewRejected(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"messageID\":\"root-m\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"part-m\",\"type\":\"text\"," +
            "\"text\":\"payload\",\"role\":\"assistant\"}}");
    }

    [TestMethod]
    public void FullReport_RootPartMessageConflict_IsUnsafe()
    {
        AssertFullReport(
            "{\"type\":\"text\",\"sessionID\":\"shared-session\",\"messageID\":\"root-m\"," +
            "\"part\":{\"id\":\"text-m\",\"messageID\":\"part-m\",\"type\":\"text\"," +
            "\"text\":\"payload\",\"role\":\"assistant\"}}",
            "shared-session", "part-m", "text-m", 0, FullReportStatus.Unsafe);
    }

    [TestMethod]
    public void Preview_RootPartSessionConflict_IsRejected()
    {
        AssertPreviewRejected(
            "{\"type\":\"text\",\"sessionID\":\"root-session\"," +
            "\"part\":{\"id\":\"text-m\",\"sessionID\":\"part-session\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\",\"role\":\"assistant\"}}");
    }

    [TestMethod]
    public void FullReport_RootPartSessionConflict_IsUnsafe()
    {
        AssertFullReport(
            "{\"type\":\"text\",\"sessionID\":\"root-session\"," +
            "\"part\":{\"id\":\"text-m\",\"sessionID\":\"part-session\",\"messageID\":\"m\"," +
            "\"type\":\"text\",\"text\":\"payload\",\"role\":\"assistant\"}}",
            "root-session", "m", "text-m", 0, FullReportStatus.Unsafe);
    }

    private static void AssertPreviewRejected(string record)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));
        var activity = Read(workspace);
        Assert.IsNull(activity.Doing);
        Assert.IsNull(activity.Done);
    }

    private static void AssertPreviewAccepted(string record, string expectedText)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));
        var activity = Read(workspace);
        Assert.AreEqual(expectedText, activity.Doing?.Text);
    }

    private static void AssertFullReport(
        string record,
        string session,
        string message,
        string part,
        long offset,
        FullReportStatus expected,
        string? expectedText = null)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        File.WriteAllBytes(workspace.LogPath("job"), Encoding.UTF8.GetBytes(record + "\n"));
        var result = ReadFullReport(
            workspace,
            new FullReportTarget(
                "job", session, message, part, offset, FirstRecordDigest(workspace)));
        Assert.AreEqual(expected, result.Status, result.Reason);
        if (expected == FullReportStatus.Complete)
        {
            Assert.AreEqual(expectedText, result.Text);
        }
        else
        {
            Assert.IsNull(result.Text);
        }
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

    private static FullReportTarget Target(
        string message,
        long? offset = null,
        string? digest = null) =>
        new("job", "shared-session", message, "text-" + message, offset, digest);

    private static FullReportTarget TargetPart(
        string message,
        string part,
        long? offset = null,
        string? digest = null) =>
        new("job", "shared-session", message, part, offset, digest);

    private static string FirstRecordDigest(TestWorkspace workspace, string jobId = "job")
    {
        var bytes = File.ReadAllBytes(workspace.LogPath(jobId));
        var end = Array.IndexOf(bytes, (byte)'\n');
        Assert.IsTrue(end >= 0, "Synthetic record must be newline-terminated.");
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, end)));
    }

    private static FullReportResult ReadFullReport(
        TestWorkspace workspace,
        FullReportTarget target,
        LedgerSourceIdentityReader? identityReader = null) =>
        ReadFullReport(workspace, Prepare(workspace, identityReader: identityReader), target);

    private static FullReportResult ReadFullReport(
        TestWorkspace workspace,
        PreparedActivity prepared,
        FullReportTarget target,
        Action? beforeContentRead = null,
        Action? beforePublishCheck = null) =>
        JobLiveActivityReader.ReadFullReport(
            prepared.Job,
            prepared.Evidence,
            prepared.Observation,
            target,
            workspace.RunsRoot,
            Now,
            CancellationToken.None,
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
        string message = "msg_1",
        string? role = "assistant") => JsonSerializer.Serialize(new
        {
            type = "tool_use", sessionID = "shared-session", timestamp, role,
            part = new
            {
                id = "tool-" + message, partID = "tool-" + message, sessionID = "shared-session",
                messageID = message, type = "tool", tool,
                state = start is not null && end is not null
                    ? new { status, time = new { start, end } }
                    : (object)new { status }
            }
        });

    private sealed class RunningServiceHarness : IAsyncDisposable
    {
        private readonly Channel<MonitoringSnapshot> _snapshots =
            Channel.CreateUnbounded<MonitoringSnapshot>();
        private readonly CancellationTokenSource _cts = new();
        private Task? _task;

        public void Start(ForgeMonitoringService service)
        {
            _task = Task.Run(async () =>
            {
                try
                {
                    await service.RunAsync(
                        snapshot => _snapshots.Writer
                            .WriteAsync(snapshot, _cts.Token)
                            .AsTask(),
                        _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected during test shutdown.
                }
                finally
                {
                    _snapshots.Writer.TryComplete();
                }
            });
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
                    throw new TimeoutException(
                        "The expected monitoring snapshot did not arrive.");
                }

                using var timeoutCts = new CancellationTokenSource(remaining);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token,
                    timeoutCts.Token);
                MonitoringSnapshot snapshot;
                try
                {
                    snapshot = await _snapshots.Reader.ReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The expected monitoring snapshot did not arrive.");
                }

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

            if (_task is not null)
            {
                try
                {
                    await _task;
                }
                catch (OperationCanceledException)
                {
                    // Expected during test shutdown.
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

    private sealed record PreparedActivity(
        ForgeJobMetadata Job,
        LiveActivitySourceEvidence Evidence,
        LedgerSourceObservation Observation);
}
