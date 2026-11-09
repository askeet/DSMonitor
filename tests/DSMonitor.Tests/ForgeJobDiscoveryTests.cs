using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ForgeJobDiscoveryTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void Discover_MissingDirectory_IsNonFatal()
    {
        var missing = Path.Combine(_workspace.Root, "does-not-exist");
        var discovery = new ForgeJobDiscovery(missing);

        var result = discovery.Discover();

        Assert.IsFalse(result.DirectoryAvailable);
        Assert.AreEqual(0, result.Jobs.Count);
        Assert.IsTrue(result.Warnings.Count > 0);
    }

    [TestMethod]
    public void Discover_ValidAndMalformedMetadata_AreBothSurfaced()
    {
        _workspace.WriteMetadata("job_good_0001");
        _workspace.WriteRawMetadata("job_bad_0001.json", "{ broken");
        File.WriteAllText(Path.Combine(_workspace.RunsRoot, "notes.txt"), "ignored");

        var discovery = new ForgeJobDiscovery(_workspace.RunsRoot);
        var result = discovery.Discover();

        Assert.IsTrue(result.DirectoryAvailable);
        Assert.AreEqual(2, result.Jobs.Count);
        var good = result.Jobs.Single(job => job.JobId == "job_good_0001");
        var bad = result.Jobs.Single(job => job.JobId == "job_bad_0001");
        Assert.AreEqual(ForgeJobStatus.Unknown, good.ExplicitStatus);
        Assert.AreEqual(ForgeJobStatus.Unknown, bad.ExplicitStatus);
        Assert.IsTrue(bad.Warnings.Count > 0);
    }

    [TestMethod]
    public void Discover_DuplicateJobId_IsIgnoredWithWarning()
    {
        _workspace.WriteRawMetadata(
            "job_first.json",
            """
            {"id":"job_same","started":1800000000000,"deadline":1800000600000}
            """);
        _workspace.WriteRawMetadata(
            "job_second.json",
            """
            {"id":"job_same","started":1800000005000,"deadline":1800000605000}
            """);

        var discovery = new ForgeJobDiscovery(_workspace.RunsRoot);
        var result = discovery.Discover();

        Assert.AreEqual(1, result.Jobs.Count);
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains("Duplicate job id", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Discover_UnassignedLog_IsReportedBoundedAndNeverBecomesAJob()
    {
        _workspace.WriteMetadata("job_associated_0001");
        _workspace.WriteLog("job_associated_0001", TestWorkspace.StepStart(1_800_000_000_000));
        var orphan = Path.Combine(_workspace.RunsRoot, "orphan.log");
        File.WriteAllText(orphan, "{}\n");

        var result = new ForgeJobDiscovery(_workspace.RunsRoot).Discover();

        Assert.AreEqual(1, result.Jobs.Count);
        Assert.AreEqual(1, result.UnassignedLogCount);
        Assert.AreEqual(Path.GetFullPath(orphan), result.UnassignedLogs.Single());
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains("unassigned", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Discover_CustomMetadataPath_DoesNotSilentlyAssociateFallbackLog()
    {
        var jobId = "job_custom_0001";
        var customPath = Path.Combine(_workspace.RunsRoot, "custom.log");
        File.WriteAllText(customPath, "{}\n");
        _workspace.WriteMetadata(jobId, logPath: customPath);
        var fallbackPath = _workspace.LogPath(jobId);
        File.WriteAllText(fallbackPath, "{}\n");

        var result = new ForgeJobDiscovery(_workspace.RunsRoot).Discover();

        Assert.AreEqual(1, result.Jobs.Count);
        Assert.AreEqual(customPath, result.Jobs[0].LogPath);
        Assert.AreEqual(1, result.UnassignedLogCount);
        Assert.AreEqual(Path.GetFullPath(fallbackPath), result.UnassignedLogs.Single());
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains("unassigned", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Discover_OrdersByStartedDescending()
    {
        _workspace.WriteMetadata("job_old_0001", 1_800_000_000_000);
        _workspace.WriteMetadata("job_new_0001", 1_800_000_100_000);

        var result = new ForgeJobDiscovery(_workspace.RunsRoot).Discover();

        Assert.AreEqual("job_new_0001", result.Jobs[0].JobId);
        Assert.AreEqual("job_old_0001", result.Jobs[1].JobId);
    }
}
