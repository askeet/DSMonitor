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
        : this(
            MonitoringComposition.CreateForStartup(),
            OpenCodeOriginalRequestService.CreateProduction())
    {
    }

    /// <summary>
    /// Creates the window with an explicit composition. Callers that do not
    /// supply a request service get the safe disabled implementation so
    /// synthetic UI tests never start an OpenCode CLI process; production
    /// startup uses the parameterless constructor.
    /// </summary>
    public MainWindow(
        MonitoringComposition composition,
        IOriginalRequestService? originalRequestService = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        InitializeComponent();

        Composition = composition;
        _viewModel = new MainViewModel(
            () => composition.CreateService(),
            Dispatcher,
            composition.Options.RunsRoot,
            composition.Options.RefreshInterval,
            originalRequestService ?? DisabledOriginalRequestService.Instance);

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
