using System.ComponentModel;
using System.Windows;
using DSMonitor.Services;
using DSMonitor.ViewModels;

namespace DSMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _closePending;

    public MainWindow()
        : this(MonitoringComposition.CreateForStartup())
    {
    }

    public MainWindow(MonitoringComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        InitializeComponent();

        Composition = composition;
        _viewModel = new MainViewModel(
            () => composition.CreateService(),
            Dispatcher,
            composition.Options.RunsRoot,
            composition.Options.RefreshInterval);

        DataContext = _viewModel;
    }

    public MonitoringComposition Composition { get; }

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
