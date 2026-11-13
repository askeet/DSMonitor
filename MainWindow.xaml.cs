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
    private bool _isOriginalRequestExpanded = true;
    private bool _isSelectedMessageExpanded = true;

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
        UpdateLiveActivityLayout();
    }

    public MonitoringComposition Composition { get; }

    private void OriginalRequestCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _isOriginalRequestExpanded = !_isOriginalRequestExpanded;
        UpdateLiveActivityLayout();
    }

    private void SelectedMessageCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _isSelectedMessageExpanded = !_isSelectedMessageExpanded;
        UpdateLiveActivityLayout();
    }

    private void OriginalRequestShowAllButton_Click(object sender, RoutedEventArgs e) =>
        FullTextDialog.Show(this, "Original Request", OriginalRequestTextBox.Text, OriginalRequestTextBox.FontSize);

    private void SelectedMessageShowAllButton_Click(object sender, RoutedEventArgs e) =>
        FullTextDialog.Show(this, "Response", SelectedActivityTextBox.Text, SelectedActivityTextBox.FontSize);

    private void UpdateLiveActivityLayout()
    {
        if (!IsInitialized || LiveActivityGrid.RowDefinitions.Count != 6)
        {
            return;
        }

        LiveActivityGrid.RowDefinitions[1].Height = _isOriginalRequestExpanded
            ? (_isSelectedMessageExpanded ? new GridLength(76) : new GridLength(1, GridUnitType.Star))
            : new GridLength(0);
        LiveActivityGrid.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        LiveActivityGrid.RowDefinitions[5].Height = _isSelectedMessageExpanded
            ? new GridLength(2, GridUnitType.Star)
            : new GridLength(0);

        OriginalRequestTextBox.Visibility = _isOriginalRequestExpanded ? Visibility.Visible : Visibility.Collapsed;
        SelectedActivityTextBox.Visibility = _isSelectedMessageExpanded ? Visibility.Visible : Visibility.Collapsed;
        OriginalRequestCollapseButton.Content = _isOriginalRequestExpanded ? "−" : "+";
        OriginalRequestCollapseButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _isOriginalRequestExpanded ? "Collapse original request" : "Expand original request");
        SelectedMessageCollapseButton.Content = _isSelectedMessageExpanded ? "−" : "+";
        SelectedMessageCollapseButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _isSelectedMessageExpanded ? "Collapse response" : "Expand response");
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
