using DSMonitor.Parsing;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class RealForgeDataSmokeTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public void ActualForgeRuns_ParseAndTailReadOnly_WithoutThrowing()
    {
        var runsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".forge-delegate",
            "runs");

        if (!Directory.Exists(runsRoot))
        {
            Assert.Inconclusive("No local forge-delegate runs directory is available.");
            return;
        }

        var discovery = new ForgeJobDiscovery(runsRoot);
        var result = discovery.Discover();

        Assert.IsTrue(result.DirectoryAvailable);
        foreach (var metadata in result.Jobs)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(metadata.JobId));
            Assert.IsTrue(PathSafety.IsWithinRoot(metadata.LogPath, runsRoot));

            var reader = new ForgeLogTailReader(
                metadata.JobId,
                metadata.LogPath!,
                runsRoot,
                maxBytesPerPoll: 128 * 1024);
            var poll = reader.Poll(DateTimeOffset.UtcNow);

            Assert.IsFalse(poll.LogMissing && !File.Exists(metadata.LogPath));
            Assert.IsTrue(reader.MalformedLineCount >= 0);
            Assert.IsTrue(poll.NewEvents.Count >= 0);
        }
    }
}
