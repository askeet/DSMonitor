using System.ComponentModel;
using System.IO;
using System.Windows;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;

namespace DSMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _closePending;

    public MainWindow()
    {
        InitializeComponent();

        var runsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".forge-delegate",
            "runs");
        var logsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSMonitor",
            "Logs");
        var refreshInterval = TimeSpan.FromSeconds(5);

        _viewModel = new MainViewModel(
            () => new ForgeMonitoringService(
                runsRoot,
                new MonitoringRunLoggerFactory(logsRoot),
                new MonitoringOptions(refreshInterval)),
            Dispatcher,
            runsRoot,
            refreshInterval);

        DataContext = _viewModel;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        if (_allowClose)
        {
            return;
        }

        if (!_viewModel.IsMonitoring)
        {
            _viewModel.Dispose();
            return;
        }

        e.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        try
        {
            await _viewModel.StopAsync();
        }
        finally
        {
            _allowClose = true;
            _viewModel.Dispose();
            Close();
        }
    }
}
