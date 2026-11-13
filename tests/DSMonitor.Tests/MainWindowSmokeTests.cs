using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DSMonitor.Models;
using DSMonitor.Parsing;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class MainWindowSmokeTests
{
    private TestWorkspace _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = new TestWorkspace();

    [TestCleanup]
    public void Cleanup() => _workspace.Dispose();

    [TestMethod]
    public void MainWindow_LoadsLaysOutAndRendersWithoutException()
    {
        var window = CreateWindow();
        try
        {
            ShowAndLayout(window);
            Assert.IsNotNull(window.DataContext);
            Assert.IsTrue(window.ActualWidth > 0);
            Assert.IsTrue(window.ActualHeight > 0);

            var bitmap = Render(window);
            Assert.IsTrue(bitmap.PixelWidth > 0);
            Assert.IsTrue(bitmap.PixelHeight > 0);

            var previewPath = Path.Combine(Path.GetTempPath(), "DSMonitor-ui-smoke.png");
            SavePng(bitmap, previewPath);
            Assert.IsTrue(new FileInfo(previewPath).Length > 10_000);
        }
        finally
        {
            window.Close();
        }
    }

    [TestMethod]
    public void MainWindow_PopulatedStateRendersAndShowsRealDataControls()
    {
        var now = DateTimeOffset.UtcNow;
        var runningJob = "job_mw8vbgms_9e9j";
        var doneJob = "job_mw720wzm_4xgv";
        var unknownJob = "job_mw80yg1g_usk9";

        _workspace.WriteMetadata(
            runningJob,
            now.AddMinutes(-38).ToUnixTimeMilliseconds(),
            now.AddMinutes(52).ToUnixTimeMilliseconds(),
            status: "RUNNING",
            directory: @"D:\Repositories\WPF\DSMonitor");
        _workspace.WriteLog(
            runningJob,
            TestWorkspace.StepStart(now.AddMinutes(-38).ToUnixTimeMilliseconds(), part: "rp1"),
            TestWorkspace.ToolUse(
                now.AddMinutes(-36).ToUnixTimeMilliseconds(),
                "read",
                "completed",
                now.AddMinutes(-36).ToUnixTimeMilliseconds(),
                now.AddMinutes(-36).AddSeconds(2).ToUnixTimeMilliseconds(),
                part: "rp2"),
            TestWorkspace.Text(now.AddMinutes(-35).ToUnixTimeMilliseconds(), "implementation notes", part: "rp3"),
            TestWorkspace.ToolUse(
                now.AddMinutes(-34).ToUnixTimeMilliseconds(),
                "shell",
                "completed",
                now.AddMinutes(-34).ToUnixTimeMilliseconds(),
                now.AddMinutes(-34).AddSeconds(8).ToUnixTimeMilliseconds(),
                part: "rp4"),
            TestWorkspace.StepFinish(now.AddMinutes(-33).ToUnixTimeMilliseconds(), 0.0021, part: "rp5"));

        _workspace.WriteMetadata(
            doneJob,
            now.AddHours(-2).ToUnixTimeMilliseconds(),
            now.AddMinutes(-30).ToUnixTimeMilliseconds(),
            status: "DONE",
            directory: @"D:\Repositories\WPF\DSMonitor");
        _workspace.WriteLog(
            doneJob,
            TestWorkspace.StepStart(now.AddHours(-2).ToUnixTimeMilliseconds(), part: "dp1", message: "dm1"),
            TestWorkspace.ToolUse(
                now.AddHours(-2).AddMinutes(1).ToUnixTimeMilliseconds(),
                "edit",
                "completed",
                now.AddHours(-2).AddMinutes(1).ToUnixTimeMilliseconds(),
                now.AddHours(-2).AddMinutes(1).AddSeconds(3).ToUnixTimeMilliseconds(),
                part: "dp2",
                message: "dm1"),
            TestWorkspace.StepFinish(now.AddHours(-2).AddMinutes(2).ToUnixTimeMilliseconds(), 0.0014, part: "dp3", message: "dm1"));

        _workspace.WriteMetadata(
            unknownJob,
            now.AddMinutes(-7).ToUnixTimeMilliseconds(),
            now.AddMinutes(83).ToUnixTimeMilliseconds());

        var jobs = new[]
        {
            CreateJobSnapshot(runningJob, ForgeJobStatus.Running, now),
            CreateJobSnapshot(doneJob, ForgeJobStatus.Done, now),
            CreateJobSnapshot(unknownJob, ForgeJobStatus.Unknown, now)
        };

        var window = CreateWindow();
        try
        {
            var viewModel = (MainViewModel)window.DataContext;
            viewModel.ApplySnapshot(new MonitoringSnapshot
            {
                ObservedAt = now,
                State = MonitoringState.Monitoring,
                ForgeDirectoryAvailable = true,
                Jobs = jobs,
                Warnings = Array.Empty<string>()
            });

            viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == runningJob);
            ShowAndLayout(window);

            Assert.AreEqual(3, viewModel.TotalCount);
            Assert.AreEqual(1, viewModel.ActiveCount);
            Assert.AreEqual(1, viewModel.CompletedCount);
            Assert.AreEqual(1, viewModel.UnknownCount);
            Assert.IsNotNull(viewModel.SelectedJob);
            Assert.AreEqual(runningJob, viewModel.SelectedJob!.JobId);
            Assert.IsTrue(viewModel.SelectedJob.RecentEvents.Count >= 4);

            var previewPath = Path.Combine(Path.GetTempPath(), "DSMonitor-ui-populated.png");
            SavePng(Render(window), previewPath);
            Assert.IsTrue(new FileInfo(previewPath).Length > 20_000);

            var tabControl = FindVisualChild<TabControl>(window);
            Assert.IsNotNull(tabControl);
            tabControl.SelectedIndex = 1;
            window.UpdateLayout();
            var timelinePath = Path.Combine(Path.GetTempPath(), "DSMonitor-ui-timeline.png");
            SavePng(Render(window), timelinePath);
            Assert.IsTrue(new FileInfo(timelinePath).Length > 20_000);
        }
        finally
        {
            window.Close();
        }
    }

    [TestMethod]
    public void LiveActivity_CollapseStatesAreIndependentAndRemainAcrossJobChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var jobA = "job_mw8vbgms_9e9j";
        var jobB = "job_mw720wzm_4xgv";
        _workspace.WriteMetadata(jobA, now.AddMinutes(-2).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
        _workspace.WriteMetadata(jobB, now.AddMinutes(-1).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());

        var window = CreateWindow();
        try
        {
            var viewModel = (MainViewModel)window.DataContext;
            viewModel.ApplySnapshot(new MonitoringSnapshot
            {
                ObservedAt = now,
                State = MonitoringState.Monitoring,
                ForgeDirectoryAvailable = true,
                Jobs = new[]
                {
                    CreateJobSnapshot(jobA, ForgeJobStatus.Running, now),
                    CreateJobSnapshot(jobB, ForgeJobStatus.Done, now)
                },
                Warnings = Array.Empty<string>()
            });
            ShowAndLayout(window);

            var originalToggle = (Button)window.FindName("OriginalRequestCollapseButton");
            var selectedToggle = (Button)window.FindName("SelectedMessageCollapseButton");
            var originalText = (TextBox)window.FindName("OriginalRequestTextBox");
            var selectedText = (TextBox)window.FindName("SelectedActivityTextBox");
            var liveGrid = (Grid)window.FindName("LiveActivityGrid");

            Assert.AreEqual(Visibility.Visible, originalText.Visibility);
            Assert.AreEqual(Visibility.Visible, selectedText.Visibility);

            originalToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Collapsed, originalText.Visibility);
            Assert.AreEqual(Visibility.Visible, selectedText.Visibility);
            Assert.AreEqual(GridUnitType.Star, liveGrid.RowDefinitions[3].Height.GridUnitType);

            selectedToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Collapsed, selectedText.Visibility);
            Assert.AreEqual(GridUnitType.Star, liveGrid.RowDefinitions[3].Height.GridUnitType);
            Assert.IsTrue(liveGrid.RowDefinitions[3].ActualHeight >= 120);

            viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == jobA);
            Assert.AreEqual(Visibility.Collapsed, originalText.Visibility);
            Assert.AreEqual(Visibility.Collapsed, selectedText.Visibility);
            viewModel.SelectedJob = viewModel.Jobs.Single(job => job.JobId == jobB);
            Assert.AreEqual(Visibility.Collapsed, originalText.Visibility);
            Assert.AreEqual(Visibility.Collapsed, selectedText.Visibility);

            originalToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Visible, originalText.Visibility);
            Assert.AreEqual(Visibility.Collapsed, selectedText.Visibility);
            Assert.AreEqual(GridUnitType.Star, liveGrid.RowDefinitions[1].Height.GridUnitType);

            selectedToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Visible, selectedText.Visibility);
            Assert.AreEqual(new GridLength(76), liveGrid.RowDefinitions[1].Height);
        }
        finally
        {
            window.Close();
        }
    }

    [TestMethod]
    public void FullTextDialog_PreservesExactTextAndCopyAllUsesCompleteSource()
    {
        const string source = "First line\r\n  indented \u03bb\nlast line  ";
        string? copied = null;
        var dialog = FullTextDialog.Create("Response", source, 12, value => copied = value);
        var textBox = (TextBox)dialog.FindName("FullTextDialogTextBox");
        var fontPicker = (ComboBox)dialog.FindName("FullTextDialogFontPicker");
        var copyButton = (Button)dialog.FindName("FullTextDialogCopyAllButton");
        var sectionTitle = (TextBlock)dialog.FindName("FullTextDialogTitle");

        Assert.AreEqual("Response", dialog.Title);
        Assert.AreEqual(dialog.Title, sectionTitle.Text);
        Assert.AreEqual(source, textBox.Text);
        Assert.IsTrue(textBox.IsReadOnly);
        Assert.AreEqual(TextWrapping.Wrap, textBox.TextWrapping);
        Assert.AreEqual(ScrollBarVisibility.Disabled, textBox.HorizontalScrollBarVisibility);
        Assert.AreEqual(12, textBox.FontSize);
        Assert.AreEqual(12, fontPicker.Items[0]);

        fontPicker.SelectedItem = 16d;
        Assert.AreEqual(16d, textBox.FontSize);

        copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(source, copied);
    }

    private ForgeJobSnapshot CreateJobSnapshot(
        string jobId,
        ForgeJobStatus status,
        DateTimeOffset observedAt)
    {
        var metadata = ForgeMetadataParser.ParseFile(
            Path.Combine(_workspace.RunsRoot, jobId + ".json"),
            _workspace.RunsRoot);

        var events = new List<MonitoringEvent>();
        if (File.Exists(_workspace.LogPath(jobId)))
        {
            foreach (var line in File.ReadAllLines(_workspace.LogPath(jobId)))
            {
                if (ForgeEventParser.TryParse(line, jobId, observedAt, out var parsed) && parsed is not null)
                {
                    events.Add(parsed.Event);
                }
            }
        }

        var cost = events.Count == 0 ? (double?)null : 0.0;
        if (status == ForgeJobStatus.Running)
        {
            cost = 0.0021;
        }
        else if (status == ForgeJobStatus.Done)
        {
            cost = 0.0014;
        }

        return new ForgeJobSnapshot
        {
            Metadata = metadata,
            Status = status,
            SessionId = "ses_e4841ff98ffeDR26dSQrKvo2RZ",
            LastActivity = events.Count > 0 ? events.Max(item => item.Timestamp ?? observedAt) : null,
            ParsedEventCount = events.Count,
            ObservedCost = cost,
            Events = events,
            ObservedAt = observedAt
        };
    }

    private static MainWindow CreateWindow() =>
        new(MonitoringComposition.CreateForStartup())
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000
        };

    private static void ShowAndLayout(MainWindow window)
    {
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static RenderTargetBitmap Render(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var width = (int)Math.Max(1, content.ActualWidth);
        var height = (int)Math.Max(1, content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }

    private static void SavePng(RenderTargetBitmap bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
}
