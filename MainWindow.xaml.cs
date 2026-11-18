using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            OpenCodeOriginalRequestService.CreateProduction(),
            OpenCodeActivityDurationService.CreateProduction())
    {
    }

    /// <summary>
    /// Creates the window with an explicit composition. Callers that do not
    /// supply observation services get the safe disabled implementations so
    /// synthetic UI tests never start an OpenCode CLI process; production
    /// startup uses the parameterless constructor.
    /// </summary>
    public MainWindow(
        MonitoringComposition composition,
        IOriginalRequestService? originalRequestService = null,
        IOpenCodeActivityDurationService? activityDurationService = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        InitializeComponent();

        Composition = composition;
        _viewModel = new MainViewModel(
            () => composition.CreateService(),
            Dispatcher,
            composition.Options.RunsRoot,
            composition.Options.RefreshInterval,
            originalRequestService ?? DisabledOriginalRequestService.Instance,
            activityDurationService ?? DisabledOpenCodeActivityDurationService.Instance);

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

    private void ActivityHistoryExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActivityHistoryDisplayRowViewModel row } &&
            row.HasChildren)
        {
            row.IsExpanded = !row.IsExpanded;
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.RefreshActivityHistoryDisplayRows();
            }
        }
    }

    private void LiveActivityHistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: ActivityHistoryDisplayRowViewModel row } &&
            DataContext is MainViewModel viewModel)
        {
            // Keep the exact parent/child display identity, not only the shared
            // activity entry, so a representative change or ItemsSource reset
            // restores the row the user actually selected.
            viewModel.SelectedActivityDisplayRow = row;
        }
    }

    private void SelectableDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid ||
            e.Key is not (Key.Up or Key.Down) ||
            Keyboard.Modifiers != ModifierKeys.None ||
            grid.Items.Count == 0)
        {
            return;
        }

        var direction = e.Key == Key.Down ? 1 : -1;
        var targetIndex = grid.SelectedIndex < 0
            ? (direction > 0 ? 0 : grid.Items.Count - 1)
            : Math.Clamp(grid.SelectedIndex + direction, 0, grid.Items.Count - 1);

        e.Handled = true;
        grid.SelectedIndex = targetIndex;
        grid.ScrollIntoView(grid.Items[targetIndex]);
        grid.Focus();
    }

    private void LiveActivityHistoryGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Only Duration has a model-level grouped sort. Sorting a flat grid
        // would detach children from their message; Started/Status stay in the
        // deterministic timestamp order.
        e.Handled = true;
        if (sender is not DataGrid grid ||
            !string.Equals(
                e.Column.SortMemberPath,
                nameof(ActivityHistoryDisplayRowViewModel.DurationSortSeconds),
                StringComparison.Ordinal))
        {
            return;
        }

        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        foreach (var column in grid.Columns)
        {
            if (!ReferenceEquals(column, e.Column))
            {
                column.SortDirection = null;
            }
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SetActivityHistoryDurationSort(direction);
        }

        // Set the arrow after the grouped projection refresh so the ItemsSource
        // replacement cannot clear the column's sort state.
        e.Column.SortDirection = direction;
    }

    private void OriginalRequestCopyAllButton_Click(object sender, RoutedEventArgs e) =>
        CopyAllText(OriginalRequestTextBox.Text);

    private void SelectedMessageCopyAllButton_Click(object sender, RoutedEventArgs e) =>
        CopyAllText(SelectedActivityTextBox.Text);

    private static void CopyAllText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private void OriginalRequestShowAllButton_Click(object sender, RoutedEventArgs e) =>
        FullTextDialog.Show(this, "Original Request", OriginalRequestTextBox.Text, OriginalRequestTextBox.FontSize);

    private void SelectedMessageShowAllButton_Click(object sender, RoutedEventArgs e) =>
        FullTextDialog.Show(this, "Response", SelectedActivityTextBox.Text, SelectedActivityTextBox.FontSize);

    private void ActivityHistoryStatus_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SelectedMessageShowAllButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ForgeJobReportedStatus_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 &&
            sender is FrameworkElement { DataContext: JobRowViewModel job } &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedJob = job;
            FullTextDialog.Show(
                this,
                "Response",
                job.SelectedActivityText,
                SelectedActivityTextBox.FontSize);
            e.Handled = true;
        }
    }

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
