using System.Text;
using DSMonitor.Parsing;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class SafeFileAccessTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void OpenRead_ValidFileInsideRoot_Succeeds()
    {
        var path = _workspace.LogPath("job_safe_0001");
        File.WriteAllText(path, "safe content");

        using var opened = SafeFileAccess.OpenRead(path, _workspace.RunsRoot);

        Assert.AreEqual(SafeReadFailure.None, opened.Failure);
        Assert.IsNotNull(opened.Stream);
        using var reader = new StreamReader(opened.Stream);
        Assert.AreEqual("safe content", reader.ReadToEnd());
    }

    [TestMethod]
    public void OpenRead_JunctionEscape_IsRejectedBeforeContentRead()
    {
        var outside = Path.Combine(_workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        var secretLog = Path.Combine(outside, "secret.log");
        var secretMetadata = Path.Combine(outside, "secret.json");
        File.WriteAllText(
            secretLog,
            TestWorkspace.StepStart(1_800_000_000_000, session: "ses_outside") + "\n");
        File.WriteAllText(
            secretMetadata,
            """{"id":"job_outside_should_not_load","started":1800000000000,"deadline":1800000600000}""");

        var escape = Path.Combine(_workspace.RunsRoot, "escape");
        _workspace.CreateJunction(escape, outside);
        var escapedLog = Path.Combine(escape, "secret.log");
        var escapedMetadata = Path.Combine(escape, "secret.json");

        using var opened = SafeFileAccess.OpenRead(escapedLog, _workspace.RunsRoot);
        Assert.AreEqual(SafeReadFailure.ReparsePoint, opened.Failure);
        Assert.IsNull(opened.Stream);
        StringAssert.Contains(opened.Warning, "reparse");

        var reader = new ForgeLogTailReader("job_escape_0001", escapedLog, _workspace.RunsRoot);
        var poll = reader.Poll(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, poll.NewEvents.Count);
        Assert.IsFalse(poll.LogMissing);
        Assert.IsTrue(poll.Warnings.Any(warning =>
            warning.Contains("reparse", StringComparison.OrdinalIgnoreCase)));

        Assert.IsNull(ForgeLogTailReader.ReadTailPreview(escapedLog, _workspace.RunsRoot));

        var metadata = ForgeMetadataParser.ParseFile(escapedMetadata, _workspace.RunsRoot);
        Assert.AreEqual("secret", metadata.JobId);
        Assert.IsTrue(metadata.Warnings.Any(warning =>
            warning.Contains("reparse", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Discovery_RootJunction_FailsClosed()
    {
        var realRuns = Path.Combine(_workspace.Root, "real-runs");
        Directory.CreateDirectory(realRuns);
        var jobId = "job_root_link_0001";
        File.WriteAllText(
            Path.Combine(realRuns, jobId + ".json"),
            """{"id":"job_root_link_0001","started":1800000000000,"deadline":1800000600000}""");
        File.WriteAllText(
            Path.Combine(realRuns, jobId + ".log"),
            TestWorkspace.StepStart(1_800_000_000_000) + "\n");

        var linkedRoot = Path.Combine(_workspace.Root, "linked-runs");
        _workspace.CreateJunction(linkedRoot, realRuns);

        var result = new ForgeJobDiscovery(linkedRoot).Discover();

        Assert.IsFalse(result.DirectoryAvailable);
        Assert.AreEqual(0, result.Jobs.Count);
        Assert.IsTrue(result.Warnings.Any(warning =>
            warning.Contains("reparse", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void OpenRead_FileSymbolicLink_FailsClosedWhenSupported()
    {
        var outside = Path.Combine(_workspace.Root, "outside-file");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.log");
        File.WriteAllText(secret, TestWorkspace.StepStart(1_800_000_000_000) + "\n");
        var link = Path.Combine(_workspace.RunsRoot, "linked.log");

        if (!_workspace.TryCreateFileSymbolicLink(link, secret, out var win32Error))
        {
            var message = win32Error == 0
                ? "unknown Win32 error"
                : new System.ComponentModel.Win32Exception(win32Error).Message;
            Assert.Inconclusive(
                $"File symbolic links are unavailable (Win32 {win32Error}: {message}). " +
                "Manual gate: run this test under an account with SeCreateSymbolicLinkPrivilege " +
                "or Windows Developer Mode enabled; do not enable elevation or security settings automatically.");
            return;
        }

        Assert.IsTrue(File.Exists(secret));
        Assert.IsTrue(File.Exists(link));
        Assert.IsTrue(
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "The test symbolic link is not reported as a reparse point.");

        using var opened = SafeFileAccess.OpenRead(link, _workspace.RunsRoot);
        Assert.AreEqual(SafeReadFailure.ReparsePoint, opened.Failure);
        Assert.IsNull(opened.Stream);
        Assert.IsNull(ForgeLogTailReader.ReadTailPreview(link, _workspace.RunsRoot));

        var reader = new ForgeLogTailReader("job_file_link_0001", link, _workspace.RunsRoot);
        var poll = reader.Poll(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, poll.NewEvents.Count);
        Assert.IsTrue(poll.Warnings.Any(warning =>
            warning.Contains("reparse", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Reader_PathReplacedWithJunctionAfterConstruction_FailsClosed()
    {
        var jobId = "job_replaced_root_0001";
        var logPath = _workspace.LogPath(jobId);
        File.WriteAllText(logPath, TestWorkspace.StepStart(1_800_000_000_000) + "\n");
        var reader = new ForgeLogTailReader(jobId, logPath, _workspace.RunsRoot);
        Assert.AreEqual(1, reader.Poll(DateTimeOffset.UtcNow).NewEvents.Count);

        var outside = Path.Combine(_workspace.Root, "outside-replaced");
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(outside, Path.GetFileName(logPath)),
            TestWorkspace.Text(1_800_000_001_000, "outside secret", part: "outside") + "\n");

        Directory.Delete(Path.GetDirectoryName(logPath)!, recursive: true);
        _workspace.CreateJunction(_workspace.RunsRoot, outside);

        var poll = reader.Poll(DateTimeOffset.UtcNow);

        Assert.AreEqual(0, poll.NewEvents.Count);
        Assert.IsTrue(poll.Warnings.Any(warning =>
            warning.Contains("reparse", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(poll.NewEvents.Any(item =>
            item.Summary.Contains("outside", StringComparison.OrdinalIgnoreCase)));
    }
}
