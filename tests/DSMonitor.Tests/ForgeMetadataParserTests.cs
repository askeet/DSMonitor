using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ForgeMetadataParserTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Parse_ValidMetadata_MapsFieldsAndStatusRemainsUnknown()
    {
        var started = TestWorkspace.Timestamp("2026-11-08T10:00:00Z");
        var deadline = TestWorkspace.Timestamp("2026-11-08T11:30:00Z");
        var path = _workspace.WriteMetadata(
            "job_valid_0001",
            started,
            deadline,
            status: null);

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual("job_valid_0001", metadata.JobId);
        Assert.AreEqual("deepseek/deepseek-flash", metadata.Model);
        Assert.AreEqual(@"C:\work\sample", metadata.Directory);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(started), metadata.Started);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(deadline), metadata.Deadline);
        Assert.AreEqual(ForgeJobStatus.Unknown, metadata.ExplicitStatus);
        Assert.IsFalse(metadata.HasExplicitStatus);
        Assert.AreEqual(_workspace.LogPath("job_valid_0001"), metadata.LogPath);
        Assert.IsFalse(metadata.IsProvisional);
    }

    [TestMethod]
    public void Parse_FractionalNumericStarted_PreservesExactTicks()
    {
        var path = _workspace.WriteRawMetadata(
            "job_precise_start.json",
            """{"id":"job_precise_start","pid":1,"started":1794084044805.0002,"deadline":1794084044905}""");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        var expected = DateTimeOffset.UnixEpoch.AddTicks(
            (1_794_084_044_805 * TimeSpan.TicksPerMillisecond) + 2);
        Assert.AreEqual(expected, metadata.Started);
        Assert.AreEqual(expected.UtcTicks, metadata.Started!.Value.UtcTicks);
    }

    [TestMethod]
    public void Parse_SubTickNumericStarted_IsRejected()
    {
        var path = _workspace.WriteRawMetadata(
            "job_sub_tick_start.json",
            """{"id":"job_sub_tick_start","pid":1,"started":1794084044805.00005,"deadline":1794084044905}""");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.IsNull(metadata.Started);
        Assert.IsTrue(metadata.Warnings.Any(warning =>
            warning.Contains("could not be parsed exactly", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Parse_MalformedJson_ReturnsUnknownPlaceholder()
    {
        var path = _workspace.WriteRawMetadata("job_broken_0001.json", "{ definitely not json");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual("job_broken_0001", metadata.JobId);
        Assert.AreEqual(ForgeJobStatus.Unknown, metadata.ExplicitStatus);
        Assert.IsTrue(metadata.IsProvisional);
        Assert.IsTrue(metadata.Warnings.Count > 0);
        Assert.IsTrue(PathSafety.IsWithinRoot(metadata.LogPath, _workspace.RunsRoot));
    }

    [TestMethod]
    public void Parse_MissingStatus_RemainsUnknownEvenAfterDeadline()
    {
        var started = TestWorkspace.Timestamp("2026-01-01T00:00:00Z");
        var deadline = TestWorkspace.Timestamp("2026-01-01T00:15:00Z");
        var path = _workspace.WriteMetadata("job_old_0001", started, deadline);

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);
        var snapshot = new ForgeJobSnapshot
        {
            Metadata = metadata,
            ObservedAt = DateTimeOffset.Parse("2026-11-08T00:00:00Z")
        };

        Assert.AreEqual(ForgeJobStatus.Unknown, snapshot.Status);
        Assert.IsTrue(snapshot.DeadlinePassed);
        StringAssert.Contains(snapshot.DeadlineDetail, "not proof", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Parse_ExplicitStatuses_AreMapped()
    {
        var cases = new (string Raw, ForgeJobStatus Expected)[]
        {
            ("RUNNING", ForgeJobStatus.Running),
            ("in_progress", ForgeJobStatus.Running),
            ("DONE", ForgeJobStatus.Done),
            ("completed", ForgeJobStatus.Done),
            ("FAILED", ForgeJobStatus.Failed),
            ("interrupted", ForgeJobStatus.Interrupted),
            ("TIMED OUT", ForgeJobStatus.TimedOut),
            ("blocked", ForgeJobStatus.Blocked)
        };

        var index = 0;
        foreach (var (raw, expected) in cases)
        {
            var jobId = $"job_status_{index++:0000}";
            var path = _workspace.WriteMetadata(jobId, status: raw);
            var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);
            Assert.AreEqual(expected, metadata.ExplicitStatus, $"raw status: {raw}");
            Assert.AreEqual(raw, metadata.RawStatus);
        }
    }

    [TestMethod]
    public void Parse_UnknownStatusText_RemainsUnknownWithWarning()
    {
        var path = _workspace.WriteMetadata("job_weird_0001", status: "teleported");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual(ForgeJobStatus.Unknown, metadata.ExplicitStatus);
        Assert.IsTrue(metadata.Warnings.Any(warning =>
            warning.Contains("not recognized", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Parse_LogPathOutsideRoot_IsIgnoredAndFallsBackInsideRoot()
    {
        var outside = Path.Combine(_workspace.Root, "outside", "other.log");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "{}");
        var path = _workspace.WriteMetadata("job_escape_0001", logPath: outside);

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.IsTrue(PathSafety.IsWithinRoot(metadata.LogPath, _workspace.RunsRoot));
        Assert.AreEqual(_workspace.LogPath("job_escape_0001"), metadata.LogPath);
        Assert.IsTrue(metadata.Warnings.Any(warning =>
            warning.Contains("outside", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Parse_RelativeLogPath_IsResolvedInsideRoot()
    {
        var path = _workspace.WriteMetadata("job_relative_0001", logPath: "job_relative_0001.log");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual(_workspace.LogPath("job_relative_0001"), metadata.LogPath);
    }

    [TestMethod]
    public void Parse_WrongFieldTypes_DoNotThrow()
    {
        var path = _workspace.WriteRawMetadata(
            "job_types_0001.json",
            """
            {
              "id": 42,
              "status": 7,
              "started": "not-a-date",
              "deadline": {},
              "logPath": ["nope"],
              "model": 12,
              "directory": null
            }
            """);

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual("job_types_0001", metadata.JobId);
        Assert.AreEqual(ForgeJobStatus.Unknown, metadata.ExplicitStatus);
        Assert.IsNull(metadata.Started);
        Assert.IsNull(metadata.Deadline);
        Assert.IsTrue(metadata.Warnings.Count >= 2);
    }

    [TestMethod]
    public void Parse_NonObjectMetadata_ReturnsUnknownPlaceholder()
    {
        var path = _workspace.WriteRawMetadata("job_array_0001.json", "[1,2,3]");

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual("job_array_0001", metadata.JobId);
        Assert.AreEqual(ForgeJobStatus.Unknown, metadata.ExplicitStatus);
        Assert.IsTrue(metadata.IsProvisional);
        StringAssert.Contains(string.Join(" ", metadata.Warnings), "not a JSON object");
    }

    [TestMethod]
    public void Parse_JobIdTraversal_IsReplacedWithFileName()
    {
        var path = _workspace.WriteRawMetadata(
            "job_safe_0001.json",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = "..\\escape",
                ["started"] = 1_800_000_000_000,
                ["deadline"] = 1_800_000_600_000,
                ["logPath"] = _workspace.LogPath("job_safe_0001")
            }));

        var metadata = ForgeMetadataParser.ParseFile(path, _workspace.RunsRoot);

        Assert.AreEqual("job_safe_0001", metadata.JobId);
        Assert.IsTrue(metadata.IsProvisional);
        Assert.IsTrue(metadata.Warnings.Any(warning =>
            warning.Contains("invalid", StringComparison.OrdinalIgnoreCase)));
    }
}
