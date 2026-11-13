using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class OriginalRequestUiTests
{
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
    public void OriginalRequestTextBox_BindsExactTextAndAttribution()
    {
        var text = "Résumé 🚀\nsecond line";
        var service = new ImmediateOriginalRequestService(
            OpenCodeOriginalRequestResult.BestEffortSingleton(text));
        var dataRoot = Path.Combine(_workspace.Root, "data");
        var composition = new MonitoringComposition(new MonitoringCompositionOptions(
            _workspace.RunsRoot,
            _workspace.LogsRoot,
            Path.Combine(dataRoot, "events-v3.db"),
            dataRoot,
            TimeSpan.FromSeconds(1)));
        var window = new MainWindow(composition, service)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000
        };

        try
        {
            var viewModel = (MainViewModel)window.DataContext;
            viewModel.ApplySnapshot(Snapshot("job_a", "ses_a"));
            viewModel.SelectedJob = viewModel.Jobs.Single();
            window.Show();
            window.UpdateLayout();

            var textBox = window.FindName("OriginalRequestTextBox") as TextBox;
            Assert.IsNotNull(textBox);
            Assert.IsTrue(textBox.IsReadOnly);
            Assert.IsTrue(textBox.AcceptsReturn);
            var textBinding = BindingOperations.GetBinding(
                textBox,
                TextBox.TextProperty);
            Assert.IsNotNull(textBinding);
            Assert.AreEqual("OriginalRequestText", textBinding.Path.Path);
            Assert.AreEqual(BindingMode.OneWay, textBinding.Mode);
            var tooltipBinding = BindingOperations.GetBinding(
                textBox,
                FrameworkElement.ToolTipProperty);
            Assert.IsNotNull(tooltipBinding);
            Assert.AreEqual("OriginalRequestAttributionText", tooltipBinding.Path.Path);

            PumpUntil(
                () =>
                {
                    window.UpdateLayout();
                    return string.Equals(textBox.Text, text, StringComparison.Ordinal);
                },
                "the rendered original request");

            Assert.AreEqual(text, textBox.Text);
            Assert.IsInstanceOfType(textBox.ToolTip, typeof(string));
            StringAssert.Contains((string)textBox.ToolTip, "Best-effort singleton");
            Assert.AreEqual(
                viewModel.SelectedJob!.OriginalRequestAttributionText,
                textBox.ToolTip);
        }
        finally
        {
            window.Close();
        }
    }

    private static MonitoringSnapshot Snapshot(string jobId, string? sessionId) => new()
    {
        ObservedAt = DateTimeOffset.UtcNow,
        State = MonitoringState.Idle,
        ForgeDirectoryAvailable = true,
        Jobs = new[]
        {
            new ForgeJobSnapshot
            {
                Metadata = new ForgeJobMetadata
                {
                    JobId = jobId,
                    MetadataPath = Path.Combine(@"C:\synthetic", jobId + ".json"),
                    LogPath = Path.Combine(@"C:\synthetic", jobId + ".log")
                },
                Status = ForgeJobStatus.Unknown,
                SessionId = sessionId,
                ObservedAt = DateTimeOffset.UtcNow,
                LiveActivity = new LiveActivitySnapshot { IsHealthy = true }
            }
        }
    };

    private static void PumpUntil(Func<bool> condition, string description)
    {
        if (condition())
        {
            return;
        }

        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        void Tick(object? sender, EventArgs args)
        {
            if (condition() || stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
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

    private sealed class ImmediateOriginalRequestService : IOriginalRequestService
    {
        private readonly OpenCodeOriginalRequestResult _result;

        public ImmediateOriginalRequestService(OpenCodeOriginalRequestResult result)
        {
            _result = result;
        }

        public Task<OpenCodeOriginalRequestResult> GetOriginalRequestAsync(
            string jobId,
            string sessionId,
            string? verifiedUserMessageId,
            CancellationToken cancellationToken,
            DateTimeOffset? jobStartedAt = null,
            IReadOnlyList<string>? observedAssistantMessageIds = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }

        public void InvalidateJob(string jobId)
        {
        }

        public void ClearCache()
        {
        }

        public void Dispose()
        {
        }
    }
}
