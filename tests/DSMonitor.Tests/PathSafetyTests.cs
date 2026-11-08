using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class PathSafetyTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void IsWithinRoot_AcceptsChildAndRejectsEscape()
    {
        Assert.IsTrue(PathSafety.IsWithinRoot(
            Path.Combine(_workspace.RunsRoot, "job.log"),
            _workspace.RunsRoot));
        Assert.IsFalse(PathSafety.IsWithinRoot(
            Path.Combine(_workspace.Root, "outside.log"),
            _workspace.RunsRoot));
        Assert.IsFalse(PathSafety.IsWithinRoot(
            Path.Combine(_workspace.RunsRoot, "..", "outside.log"),
            _workspace.RunsRoot));
        Assert.IsFalse(PathSafety.IsWithinRoot(
            _workspace.RunsRoot + "2" + Path.DirectorySeparatorChar + "job.log",
            _workspace.RunsRoot));
    }

    [TestMethod]
    public void IsSafeFileName_RejectsTraversalAndSeparators()
    {
        Assert.IsTrue(PathSafety.IsSafeFileName("job_abc_1234"));
        Assert.IsFalse(PathSafety.IsSafeFileName(".."));
        Assert.IsFalse(PathSafety.IsSafeFileName("..\\escape"));
        Assert.IsFalse(PathSafety.IsSafeFileName("folder/job.json"));
        Assert.IsFalse(PathSafety.IsSafeFileName("bad:name"));
        Assert.IsFalse(PathSafety.IsSafeFileName("  "));
    }
}
