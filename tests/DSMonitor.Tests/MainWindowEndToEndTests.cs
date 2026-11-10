using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Synthetic-only end-to-end WPF lifecycle coverage. All runs, logs and the V3
/// database live under a disposable TestWorkspace; no production or OpenCode
/// data is opened. The window is shown offscreen and driven through the real
/// Start/Stop commands and close lifecycle.
/// </summary>
[STATestClass]
public sealed class MainWindowEndToEndTests
{
    private const string Session = "ses_e2e_shared";
    private const string DoneHeading = "DONE · OBSERVED RESULT, UNVERIFIED";
    private TestWorkspace _workspace = null!;
    private SynchronizationContext? _priorContext;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = new TestWorkspace();
        _priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SynchronizationContext.SetSynchronizationContext(_priorContext);
        _workspace.Dispose();
    }

    [TestMethod]
    public void MainWindow_EndToEnd_V3Lifecycle_RenderedAttributionAndCleanup()
    {
        var dataRoot = Path.Combine(_workspace.Root, "data");
        var databasePath = Path.Combine(dataRoot, "events-v3.db");
        var composition = new MonitoringComposition(new MonitoringCompositionOptions(
            _workspace.RunsRoot,
            _workspace.LogsRoot,
            databasePath,
            dataRoot,
            TimeSpan.FromMilliseconds(100)));

        AssertSyntheticBeforeStart(composition, databasePath, dataRoot, _workspace.Root);
        WriteLiveJobs();
        Assert.AreEqual(2, Directory.GetFiles(_workspace.RunsRoot, "*.json").Length);

        var window = CreateWindow(composition);
        MainViewModel viewModel;
        try
        {
            viewModel = (MainViewModel)window.DataContext;
            window.Show();
            PumpOnce(window.Dispatcher);
            Assert.IsFalse(viewModel.IsMonitoring);
            Assert.AreEqual(0, viewModel.Jobs.Count, "No fabricated jobs before monitoring starts.");
            Assert.IsFalse(File.Exists(databasePath));

            viewModel.StartCommand.Execute(null);
            PumpUntil(
                window.Dispatcher,
                () => viewModel.Jobs.Count == 2 &&
                      viewModel.Jobs.All(job => job.Snapshot.LiveActivity.Doing is not null &&
                                                job.Snapshot.LiveActivity.Done is not null),
                TimeSpan.FromSeconds(15),
                "two live jobs through the real start lifecycle");

            var jobA = viewModel.Jobs.Single(job => job.JobId == "job_e2e_a");
            var jobB = viewModel.Jobs.Single(job => job.JobId == "job_e2e_b");
            AssertJob(jobA, "alpha e2e result", 0.125);
            AssertJob(jobB, "bravo e2e result", 0.25);
            Assert.AreEqual(Session, jobA.Snapshot.SessionId);
            Assert.AreEqual(Session, jobB.Snapshot.SessionId);
            Assert.AreNotEqual(jobA.Snapshot.LiveActivity.Done!.MessageId, jobB.Snapshot.LiveActivity.Done!.MessageId);

            AssertRenderedDetail(window, viewModel, jobA, "alpha e2e result");
            AssertRenderedProvenance(window, viewModel, jobA, roleless: false);
            AssertRenderedDetail(window, viewModel, jobB, "bravo e2e result");
            AssertRenderedProvenance(window, viewModel, jobB, roleless: false);

            StopAndWait(window, viewModel);
            Assert.IsFalse(viewModel.IsMonitoring);
            Assert.IsTrue(File.Exists(databasePath));

            viewModel.StartCommand.Execute(null);
            PumpUntil(
                window.Dispatcher,
                () => viewModel.Jobs.Count == 2 &&
                      viewModel.Jobs.All(job => job.Snapshot.LiveActivity.Doing is not null &&
                                                job.Snapshot.LiveActivity.Done is not null),
                TimeSpan.FromSeconds(15),
                "two jobs restored after restart");
            var restoredA = viewModel.Jobs.Single(job => job.JobId == "job_e2e_a");
            var restoredB = viewModel.Jobs.Single(job => job.JobId == "job_e2e_b");
            AssertJob(restoredA, "alpha e2e result", 0.125);
            AssertJob(restoredB, "bravo e2e result", 0.25);
            Assert.IsTrue(File.Exists(databasePath));

            WriteRejectedJobs();
            PumpUntil(
                window.Dispatcher,
                () => viewModel.Jobs.Count == 5 &&
                      viewModel.Jobs.Any(job => job.JobId == "job_e2e_no_role") &&
                      viewModel.Jobs.Any(job => job.JobId == "job_e2e_ambiguous_a") &&
                      viewModel.Jobs.Any(job => job.JobId == "job_e2e_ambiguous_b"),
                TimeSpan.FromSeconds(15),
                "roleless and ambiguous jobs through the same lifecycle");

            // Intentional requirement change: roleless records are observed,
            // but only as explicitly unverified activity.
            var noRole = viewModel.Jobs.Single(job => job.JobId == "job_e2e_no_role");
            Assert.AreEqual("unverified prompt", noRole.DoingText);
            Assert.AreEqual("unverified prompt", noRole.DoneText);
            Assert.AreEqual("unverified prompt", noRole.Snapshot.LiveActivity.Doing?.Text);
            Assert.AreEqual("unverified prompt", noRole.Snapshot.LiveActivity.Done?.Text);
            StringAssert.Contains(noRole.Snapshot.LiveActivity.Doing!.Kind, "Observed");
            StringAssert.Contains(noRole.Snapshot.LiveActivity.Doing.Kind, "unverified");
            StringAssert.Contains(noRole.Snapshot.LiveActivity.Done!.Kind, "Observed");
            StringAssert.Contains(noRole.Snapshot.LiveActivity.Done.Kind, "unverified");
            StringAssert.Contains(noRole.TaskText, "N/A");
            AssertRenderedDetail(window, viewModel, noRole, "unverified prompt");
            AssertRenderedProvenance(window, viewModel, noRole, roleless: true);

            foreach (var ambiguousId in new[] { "job_e2e_ambiguous_a", "job_e2e_ambiguous_b" })
            {
                var ambiguous = viewModel.Jobs.Single(job => job.JobId == ambiguousId);
                Assert.IsNull(ambiguous.Snapshot.LiveActivity.Doing);
                Assert.IsNull(ambiguous.Snapshot.LiveActivity.Done);
                StringAssert.Contains(ambiguous.Snapshot.LiveActivity.Availability, "multiple jobs");
                Assert.AreEqual("N/A", ambiguous.DoingText);
                Assert.AreEqual("N/A", ambiguous.DoneText);
            }

            AssertRenderedDetail(
                window,
                viewModel,
                viewModel.Jobs.Single(job => job.JobId == "job_e2e_ambiguous_a"),
                "N/A");

            StopAndWait(window, viewModel);
            window.Close();
            PumpUntil(
                window.Dispatcher,
                () => !window.IsLoaded,
                TimeSpan.FromSeconds(10),
                "window close");
            Assert.IsFalse(viewModel.IsMonitoring);
            Assert.AreEqual(MonitoringState.Stopped, viewModel.MonitoringState);
            PumpUntil(
                window.Dispatcher,
                () => CanOpenExclusively(databasePath),
                TimeSpan.FromSeconds(10),
                "released V3 database handle");
        }
        finally
        {
            CloseForCleanup(window);
        }
    }

    private void WriteLiveJobs()
    {
        _workspace.WriteMetadata("job_e2e_a", status: "RUNNING");
        _workspace.WriteLog(
            "job_e2e_a",
            AssistantText("msg-a", "alpha e2e result", 1_800_000_001_000),
            AssistantFinish("msg-a", 0.125m, 1_800_000_002_000));

        _workspace.WriteMetadata("job_e2e_b", status: "DONE");
        _workspace.WriteLog(
            "job_e2e_b",
            AssistantText("msg-b", "bravo e2e result", 1_800_000_003_000),
            AssistantFinish("msg-b", 0.25m, 1_800_000_004_000));
    }

    private void WriteRejectedJobs()
    {
        _workspace.WriteMetadata("job_e2e_no_role");
        _workspace.WriteLog(
            "job_e2e_no_role",
            NoRoleText("msg-no-role", "unverified prompt", 1_800_000_005_000),
            NoRoleFinish("msg-no-role", 0.05m, 1_800_000_006_000));

        var sharedLog = Path.Combine(_workspace.RunsRoot, "ambiguous-shared.log");
        File.WriteAllText(
            sharedLog,
            AssistantText("msg-ambig", "shared ambiguous result", 1_800_000_007_000) + "\n" +
            AssistantFinish("msg-ambig", 0.075m, 1_800_000_008_000) + "\n");
        _workspace.WriteMetadata("job_e2e_ambiguous_a", logPath: sharedLog);
        _workspace.WriteMetadata("job_e2e_ambiguous_b", logPath: sharedLog);
    }

    private static void AssertSyntheticBeforeStart(
        MonitoringComposition composition,
        string databasePath,
        string dataRoot,
        string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        foreach (var path in new[]
                 {
                     composition.Options.RunsRoot,
                     composition.Options.LogsRoot,
                     composition.Options.DatabasePath,
                     composition.Options.DatabaseRoot
                 })
        {
            Assert.IsTrue(Path.GetFullPath(path).StartsWith(
                root,
                StringComparison.OrdinalIgnoreCase), path);
        }

        Assert.IsTrue(Path.GetFullPath(databasePath).StartsWith(
            Path.GetFullPath(dataRoot),
            StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Directory.Exists(composition.Options.RunsRoot));
        Assert.IsTrue(Directory.Exists(composition.Options.LogsRoot));
        Assert.IsFalse(File.Exists(databasePath));
        Assert.IsFalse(Directory.Exists(dataRoot));
    }

    private static void AssertJob(JobRowViewModel job, string expected, double cost)
    {
        Assert.AreEqual(expected, job.Snapshot.LiveActivity.Doing?.Text);
        Assert.AreEqual(expected, job.Snapshot.LiveActivity.Done?.Text);
        Assert.AreEqual(2, job.Snapshot.ParsedEventCount);
        Assert.AreEqual(cost, job.Snapshot.ObservedCost!.Value, 1e-12);
        StringAssert.Contains(job.Snapshot.LiveActivity.Done!.Kind, "unverified");
        StringAssert.Contains(job.TaskText, "N/A");
    }

    private static void AssertRenderedDetail(
        MainWindow window,
        MainViewModel viewModel,
        JobRowViewModel job,
        string expected)
    {
        viewModel.SelectedJob = job;
        var tab = FindVisualChild<TabControl>(window);
        Assert.IsNotNull(tab);
        tab.SelectedIndex = 0;
        window.UpdateLayout();
        PumpUntil(
            window.Dispatcher,
            () =>
            {
                window.UpdateLayout();
                return HasBoundText(tab, job, "DoingText", expected) &&
                       HasBoundText(tab, job, "DoneText", expected) &&
                       HasBoundText(tab, job, "TaskText", job.TaskText);
            },
            TimeSpan.FromSeconds(5),
            $"rendered live activity for {job.JobId}");

        var doing = FindBoundTextBlocks(tab, "DoingText")
            .Where(block => ReferenceEquals(block.DataContext, job))
            .ToArray();
        var done = FindBoundTextBlocks(tab, "DoneText")
            .Where(block => ReferenceEquals(block.DataContext, job))
            .ToArray();
        var task = FindBoundTextBlocks(tab, "TaskText")
            .Where(block => ReferenceEquals(block.DataContext, job))
            .ToArray();
        Assert.IsTrue(doing.Length > 0, "Rendered Doing control was not found.");
        Assert.IsTrue(done.Length > 0, "Rendered Done control was not found.");
        Assert.IsTrue(task.Length > 0, "Rendered Task control was not found.");
        Assert.IsTrue(doing.All(block => block.Text == expected));
        Assert.IsTrue(done.All(block => block.Text == expected));
        Assert.IsTrue(task.All(block => block.Text == job.TaskText));
        StringAssert.Contains(job.TaskText, "N/A");
    }

    private static void AssertRenderedProvenance(
        MainWindow window,
        MainViewModel viewModel,
        JobRowViewModel job,
        bool roleless)
    {
        viewModel.SelectedJob = job;
        var tab = FindVisualChild<TabControl>(window);
        Assert.IsNotNull(tab);
        tab.SelectedIndex = 0;
        window.UpdateLayout();
        PumpUntil(
            window.Dispatcher,
            () =>
            {
                window.UpdateLayout();
                return FindBoundTextBlocks(tab, "DoingProvenance")
                           .Any(block => ReferenceEquals(block.DataContext, job) &&
                                         !string.IsNullOrWhiteSpace(block.Text)) &&
                       FindBoundTextBlocks(tab, "DoneProvenance")
                           .Any(block => ReferenceEquals(block.DataContext, job) &&
                                         !string.IsNullOrWhiteSpace(block.Text));
            },
            TimeSpan.FromSeconds(5),
            $"rendered provenance for {job.JobId}");

        var doingProvenance = FindBoundTextBlocks(tab, "DoingProvenance")
            .Where(block => ReferenceEquals(block.DataContext, job))
            .ToArray();
        var doneProvenance = FindBoundTextBlocks(tab, "DoneProvenance")
            .Where(block => ReferenceEquals(block.DataContext, job))
            .ToArray();
        Assert.IsTrue(doingProvenance.Length > 0, "Rendered Doing provenance was not found.");
        Assert.IsTrue(doneProvenance.Length > 0, "Rendered Done provenance was not found.");

        var heading = FindTextBlocks(tab)
            .Where(block => block.Text.Contains("DONE ·", StringComparison.Ordinal))
            .ToArray();
        Assert.IsTrue(heading.Any(block => block.Text == DoneHeading), "Neutral Done heading was not rendered.");
        Assert.IsFalse(
            heading.Any(block => block.Text.Contains(
                "AGENT-REPORTED", StringComparison.OrdinalIgnoreCase)),
            "The static Done heading must not assert agent attribution.");

        if (roleless)
        {
            Assert.IsTrue(doingProvenance.All(block =>
                block.Text.Contains("Observed roleless text", StringComparison.Ordinal) &&
                block.Text.Contains("unverified", StringComparison.Ordinal)));
            Assert.IsTrue(doneProvenance.All(block =>
                block.Text.Contains("Observed roleless result", StringComparison.Ordinal) &&
                block.Text.Contains("unverified", StringComparison.Ordinal)));
            Assert.IsFalse(doingProvenance.Any(block =>
                block.Text.Contains("Assistant", StringComparison.Ordinal)));
            Assert.IsFalse(doneProvenance.Any(block =>
                block.Text.Contains("Agent-reported", StringComparison.Ordinal)));
        }
        else
        {
            Assert.IsTrue(doingProvenance.All(block =>
                block.Text.Contains("Assistant-reported text", StringComparison.Ordinal)));
            Assert.IsTrue(doneProvenance.All(block =>
                block.Text.Contains("Agent-reported result", StringComparison.Ordinal) &&
                block.Text.Contains("unverified", StringComparison.Ordinal)));
        }
    }

    private static IReadOnlyList<TextBlock> FindTextBlocks(DependencyObject root)
    {
        var results = new List<TextBlock>();
        Walk(root);
        return results;

        void Walk(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is TextBlock block)
                {
                    results.Add(block);
                }

                Walk(child);
            }
        }
    }

    private static bool HasBoundText(
        DependencyObject root,
        JobRowViewModel job,
        string path,
        string expected) =>
        FindBoundTextBlocks(root, path).Any(block =>
            ReferenceEquals(block.DataContext, job) &&
            string.Equals(block.Text, expected, StringComparison.Ordinal));

    private static IReadOnlyList<TextBlock> FindBoundTextBlocks(
        DependencyObject root,
        string path)
    {
        var results = new List<TextBlock>();
        Walk(root);
        return results;

        void Walk(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is TextBlock block &&
                    BindingOperations.GetBindingExpression(block, TextBlock.TextProperty) is { } expression &&
                    string.Equals(expression.ParentBinding.Path?.Path, path, StringComparison.Ordinal))
                {
                    results.Add(block);
                }

                Walk(child);
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void StopAndWait(MainWindow window, MainViewModel viewModel)
    {
        viewModel.StopCommand.Execute(null);
        PumpUntil(
            window.Dispatcher,
            () => !viewModel.IsMonitoring && viewModel.StartCommand.CanExecute(null),
            TimeSpan.FromSeconds(15),
            "monitoring stop");
    }

    private static void CloseForCleanup(MainWindow? window)
    {
        if (window is null || !window.IsLoaded)
        {
            return;
        }

        try
        {
            window.Close();
            PumpUntil(
                window.Dispatcher,
                () => !window.IsLoaded,
                TimeSpan.FromSeconds(10),
                "cleanup window close");
        }
        catch
        {
            // Preserve the original test failure.
        }
    }

    private static void PumpOnce(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void PumpUntil(
        Dispatcher dispatcher,
        Func<bool> condition,
        TimeSpan timeout,
        string description)
    {
        if (condition())
        {
            return;
        }

        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        void Tick(object? sender, EventArgs args)
        {
            if (condition() || stopwatch.Elapsed >= timeout)
            {
                frame.Continue = false;
            }
        }

        timer.Tick += Tick;
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Tick -= Tick;
            timer.Stop();
        }

        Assert.IsTrue(condition(), $"Timed out waiting for {description}.");
    }

    private static MainWindow CreateWindow(MonitoringComposition composition) =>
        new(composition)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000
        };

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string AssistantText(string message, string text, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "text",
            sessionID = Session,
            timestamp,
            role = "assistant",
            part = new
            {
                id = "text-" + message,
                messageID = message,
                type = "text",
                text
            }
        });

    private static string AssistantFinish(string message, decimal cost, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "step_finish",
            sessionID = Session,
            timestamp,
            role = "assistant",
            part = new
            {
                id = "finish-" + message,
                messageID = message,
                type = "step-finish",
                reason = "stop",
                cost
            }
        });

    private static string NoRoleText(string message, string text, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "text",
            sessionID = Session,
            timestamp,
            part = new
            {
                id = "text-" + message,
                messageID = message,
                type = "text",
                text,
                role = (string?)null
            }
        });

    private static string NoRoleFinish(string message, decimal cost, long timestamp) =>
        JsonSerializer.Serialize(new
        {
            type = "step_finish",
            sessionID = Session,
            timestamp,
            part = new
            {
                id = "finish-" + message,
                messageID = message,
                type = "step-finish",
                reason = "stop",
                cost,
                role = (string?)null
            }
        });
}
