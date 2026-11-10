using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class MonitoringCompositionWpfTests
{
    [TestMethod]
    public void MainWindow_WithExplicitSyntheticComposition_DoesNotOpenOrStartAnything()
    {
        using var workspace = new TestWorkspace();
        var dataRoot = Path.Combine(workspace.Root, "data");
        var composition = new MonitoringComposition(new MonitoringCompositionOptions(
            workspace.RunsRoot,
            workspace.LogsRoot,
            Path.Combine(dataRoot, "events-v3.db"),
            dataRoot,
            TimeSpan.FromSeconds(1)));

        var window = new MainWindow(composition);
        try
        {
            Assert.AreSame(composition, window.Composition);
            Assert.IsInstanceOfType(window.DataContext, typeof(MainViewModel));
            Assert.IsFalse(File.Exists(composition.Options.DatabasePath));
            Assert.IsFalse(Directory.GetFiles(
                composition.Options.LogsRoot,
                "*",
                SearchOption.AllDirectories).Any());
            Assert.IsFalse(Directory.Exists(dataRoot));
        }
        finally
        {
            window.Close();
        }
    }
}
