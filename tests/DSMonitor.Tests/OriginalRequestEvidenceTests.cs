using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Persistence;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class OriginalRequestEvidenceTests
{
    [TestMethod]
    public void Collect_SeparatesJobsAndSessions()
    {
        var jobA = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a_text", "Assistant-reported text"),
            Entry("job_a", "ses_a", "msg_a_tool", "Observed tool activity"));
        var jobB = Snapshot(
            "job_b",
            "ses_b",
            Entry("job_b", "ses_b", "msg_b_text", "Assistant-reported text"));

        var evidenceA = OriginalRequestEvidence.Collect(jobA, "ses_a");
        var evidenceB = OriginalRequestEvidence.Collect(jobB, "ses_b");

        CollectionAssert.AreEquivalent(
            new[] { "msg_a_text", "msg_a_tool" },
            evidenceA.ToArray());
        CollectionAssert.AreEqual(new[] { "msg_b_text" }, evidenceB.ToArray());
        CollectionAssert.DoesNotContain(evidenceA.ToArray(), "msg_b_text");
        CollectionAssert.DoesNotContain(evidenceB.ToArray(), "msg_a_text");
    }

    [TestMethod]
    public void Collect_IncludesRolelessStructuredKinds_ButNotUnknownKinds()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_verified", "Assistant-reported text"),
            Entry("job_a", "ses_a", "msg_roleless", "Observed roleless text — unverified"),
            Entry("job_a", "ses_a", "msg_roleless_tool", "Observed roleless tool activity — unverified"),
            Entry("job_a", "ses_a", "msg_roleless_result", "Observed roleless result — unverified"),
            Entry("job_a", "ses_a", "msg_unknown", "User-authored text"));

        var evidence = OriginalRequestEvidence.Collect(snapshot, "ses_a");

        CollectionAssert.AreEquivalent(
            new[]
            {
                "msg_verified",
                "msg_roleless",
                "msg_roleless_tool",
                "msg_roleless_result"
            },
            evidence.ToArray());
        CollectionAssert.DoesNotContain(evidence.ToArray(), "msg_unknown");
    }

    [TestMethod]
    public void Collect_MismatchedJobOrSession_RejectsAnchors()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text"),
            Entry("job_b", "ses_a", "msg_other_job", "Assistant-reported text"));

        var evidence = OriginalRequestEvidence.Collect(snapshot, "ses_a");

        Assert.AreEqual(0, evidence.Count);
    }

    [TestMethod]
    public void Collect_MixedSourcePaths_RejectsAnchors()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text", @"C:\runs\a.log"),
            Entry("job_a", "ses_a", "msg_b", "Assistant-reported text", @"C:\runs\b.log"));

        var evidence = OriginalRequestEvidence.Collect(snapshot, "ses_a");

        Assert.AreEqual(0, evidence.Count);
        Assert.IsFalse(OriginalRequestEvidence.TryCollect(
            snapshot,
            "ses_a",
            out var ids));
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void TryCollect_NoCandidateEvidence_ReturnsTrueEmpty()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_unknown", "User-authored text"));

        Assert.IsTrue(OriginalRequestEvidence.TryCollect(
            snapshot,
            "ses_a",
            out var ids));
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void Select_DistinguishesNoEvidenceConflictAndUnavailable()
    {
        var noEvidence = Snapshot("job_a", "ses_a");
        var conflict = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text", @"C:\runs\a.log"),
            Entry("job_a", "ses_a", "msg_b", "Assistant-reported text", @"C:\runs\b.log"));
        var unavailable = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text")) with
        {
            LiveActivity = new LiveActivitySnapshot { IsHealthy = false }
        };

        var none = OriginalRequestEvidence.Select(noEvidence, "ses_a");
        var conflicting = OriginalRequestEvidence.Select(conflict, "ses_a");
        var unhealthy = OriginalRequestEvidence.Select(unavailable, "ses_a");

        Assert.AreEqual(OriginalRequestEvidenceState.None, none.State);
        Assert.AreEqual(string.Empty, none.Fingerprint);
        Assert.AreEqual(OriginalRequestEvidenceState.Conflicting, conflicting.State);
        Assert.AreNotEqual(string.Empty, conflicting.Fingerprint);
        Assert.AreEqual(OriginalRequestEvidenceState.Unavailable, unhealthy.State);
        Assert.AreNotEqual(string.Empty, unhealthy.Fingerprint);
        Assert.AreNotEqual(none.Fingerprint, conflicting.Fingerprint);
        Assert.AreNotEqual(none.Fingerprint, unhealthy.Fingerprint);
        Assert.AreNotEqual(conflicting.Fingerprint, unhealthy.Fingerprint);
    }

    [TestMethod]
    public void Collect_UnsafeMessageId_RejectsAnchors()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "bad id", "Assistant-reported text"),
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text"));

        var evidence = OriginalRequestEvidence.Collect(snapshot, "ses_a");

        Assert.AreEqual(0, evidence.Count);
    }

    [TestMethod]
    public void Collect_InvalidSession_ReturnsNoAnchors()
    {
        var snapshot = Snapshot(
            "job_a",
            "ses_a",
            Entry("job_a", "ses_a", "msg_a", "Assistant-reported text"));

        Assert.AreEqual(0, OriginalRequestEvidence.Collect(snapshot, null).Count);
        Assert.AreEqual(0, OriginalRequestEvidence.Collect(snapshot, "not-a-session").Count);
        Assert.AreEqual(0, OriginalRequestEvidence.Collect(snapshot, "ses_other").Count);
    }

    [TestMethod]
    public void ProductionShapedRolelessReaderRecord_PreservesStructuredMessageId()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            "{\"type\":\"text\",\"sessionID\":\"ses_reader\",\"timestamp\":1800000000000," +
            "\"part\":{\"id\":\"text-msg_roleless\",\"messageID\":\"msg_roleless\"," +
            "\"type\":\"text\",\"text\":\"find msg_fake in text\"}}");
        var (job, activity) = ReadActivity(workspace);

        Assert.IsTrue(activity.IsHealthy);
        Assert.IsNotNull(activity.Doing);
        Assert.AreEqual("msg_roleless", activity.Doing!.MessageId);
        StringAssert.Contains(activity.Doing.Kind, "roleless");

        var snapshot = new ForgeJobSnapshot
        {
            Metadata = job,
            SessionId = "ses_reader",
            ObservedAt = DateTimeOffset.UtcNow,
            LiveActivity = activity
        };
        var evidence = OriginalRequestEvidence.Collect(snapshot, "ses_reader");

        CollectionAssert.Contains(evidence.ToArray(), "msg_roleless");
        CollectionAssert.DoesNotContain(evidence.ToArray(), "msg_fake");
    }

    [TestMethod]
    public void ProductionShapedReaderRecord_ExplicitUserRoleIsNotEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteMetadata("job");
        workspace.WriteLog(
            "job",
            "{\"type\":\"text\",\"sessionID\":\"ses_reader\",\"timestamp\":1800000000000," +
            "\"part\":{\"id\":\"text-msg_user\",\"messageID\":\"msg_user\"," +
            "\"type\":\"text\",\"text\":\"request\",\"role\":\"user\"}}");
        var (job, activity) = ReadActivity(workspace);

        Assert.IsTrue(activity.IsHealthy);
        Assert.IsNull(activity.Doing);
        var snapshot = new ForgeJobSnapshot
        {
            Metadata = job,
            SessionId = "ses_reader",
            ObservedAt = DateTimeOffset.UtcNow,
            LiveActivity = activity
        };
        Assert.AreEqual(
            0,
            OriginalRequestEvidence.Collect(snapshot, "ses_reader").Count);
    }

    [TestMethod]
    public void Select_ExceedsMessageIdBound_IsConflicting()
    {
        var entries = Enumerable
            .Range(0, OriginalRequestEvidence.MaxMessageIds + 1)
            .Select(index => Entry(
                "job_a",
                "ses_a",
                "msg_" + index,
                "Assistant-reported text"))
            .ToArray();
        var snapshot = Snapshot("job_a", "ses_a", entries);

        var selection = OriginalRequestEvidence.Select(snapshot, "ses_a");

        Assert.AreEqual(OriginalRequestEvidenceState.Conflicting, selection.State);
        Assert.AreEqual(0, selection.MessageIds.Count);
        Assert.AreNotEqual(string.Empty, selection.Fingerprint);
    }

    [TestMethod]
    public void ComputeFingerprint_IsOrderInsensitiveAndSetSensitive()
    {
        var first = OriginalRequestEvidence.ComputeFingerprint(
            "job_a",
            "ses_a",
            new[] { "msg_b", "msg_a" });
        var reordered = OriginalRequestEvidence.ComputeFingerprint(
            "job_a",
            "ses_a",
            new[] { "msg_a", "msg_b" });
        var changed = OriginalRequestEvidence.ComputeFingerprint(
            "job_a",
            "ses_a",
            new[] { "msg_a", "msg_c" });
        var otherJob = OriginalRequestEvidence.ComputeFingerprint(
            "job_b",
            "ses_a",
            new[] { "msg_a", "msg_b" });
        var otherSession = OriginalRequestEvidence.ComputeFingerprint(
            "job_a",
            "ses_b",
            new[] { "msg_a", "msg_b" });

        Assert.AreEqual(first, reordered);
        Assert.AreNotEqual(first, changed);
        Assert.AreNotEqual(first, otherJob);
        Assert.AreNotEqual(first, otherSession);
        Assert.AreEqual(string.Empty, OriginalRequestEvidence.ComputeFingerprint(
            "job_a",
            "ses_a",
            Array.Empty<string>()));
    }

    private static (ForgeJobMetadata Job, LiveActivitySnapshot Activity) ReadActivity(
        TestWorkspace workspace)
    {
        var jobs = new ForgeJobDiscovery(workspace.RunsRoot).Discover().Jobs;
        var evidence = JobLiveActivityReader.Preflight(jobs, workspace.RunsRoot);
        var job = jobs.Single(candidate => candidate.JobId == "job");
        var database = Path.Combine(workspace.Root, "activity.db");
        using var ledger = EventLedger.OpenV3Projection(database, workspace.Root);
        var now = DateTimeOffset.UtcNow;
        var ingest = ledger.Ingest(job.JobId, job.LogPath!, workspace.RunsRoot, now);
        Assert.IsTrue(ingest.IsSuccess, ingest.Status.ToString());
        Assert.IsNotNull(ingest.SourceObservation);
        var activity = JobLiveActivityReader.Read(
            job,
            evidence[job.JobId],
            ingest.SourceObservation!,
            workspace.RunsRoot,
            now);
        return (job, activity);
    }

    private static ForgeJobSnapshot Snapshot(
        string jobId,
        string sessionId,
        params LiveActivityHistoryEntry[] entries) => new()
    {
        Metadata = new ForgeJobMetadata
        {
            JobId = jobId,
            MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
            LogPath = Path.Combine(@"C:\synthetic", jobId + ".log")
        },
        SessionId = sessionId,
        ObservedAt = DateTimeOffset.UnixEpoch,
        LiveActivity = new LiveActivitySnapshot
        {
            IsHealthy = true,
            History = entries
        }
    };

    private static LiveActivityHistoryEntry Entry(
        string jobId,
        string sessionId,
        string messageId,
        string kind,
        string sourcePath = @"C:\synthetic\job.log") => new(
        jobId,
        sessionId,
        messageId,
        "part_" + messageId,
        kind,
        "text",
        sourcePath,
        0,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        "digest",
        null);
}
