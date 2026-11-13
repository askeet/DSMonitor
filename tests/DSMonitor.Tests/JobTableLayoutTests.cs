using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[STATestClass]
public sealed class JobTableLayoutTests
{
    [TestMethod]
    public void JobsGrid_ExposesExactlyTheFiveRequestedColumnsInOrder()
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
            var grid = window.FindName("JobsDataGrid") as DataGrid;
            Assert.IsNotNull(grid, "The jobs DataGrid must remain named for layout compatibility.");

            CollectionAssert.AreEqual(
                new[] { "Project", "Started", "Duration", "Job", "Reported Status" },
                grid.Columns.Select(column => column.Header?.ToString()).ToArray());

            Assert.AreEqual("DurationSortSeconds", grid.Columns[2].SortMemberPath);
            Assert.IsTrue(
                grid.Columns[0].Width.DisplayValue is >= 80 and <= 100,
                "Project is a compact final-folder column of about twelve characters.");
            Assert.IsTrue(grid.Columns[1].Width.DisplayValue <= 100);
            Assert.IsTrue(grid.Columns[2].Width.DisplayValue <= 80);
            Assert.IsTrue(grid.Columns[3].Width.DisplayValue <= 120);
            Assert.IsTrue(
                grid.Columns[4].Width.IsStar,
                "Reported Status must flex into the remaining table width; a star column has no measured width until the grid is arranged.");
            Assert.AreEqual(1d, grid.Columns[4].Width.Value);
            Assert.AreEqual(44d, grid.RowHeight);

            Assert.AreEqual(DataGridSelectionMode.Single, grid.SelectionMode);
            Assert.AreEqual(DataGridSelectionUnit.FullRow, grid.SelectionUnit);
            Assert.IsTrue(grid.IsReadOnly);

            var itemsBinding = BindingOperations.GetBinding(grid, ItemsControl.ItemsSourceProperty);
            Assert.IsNotNull(itemsBinding);
            Assert.AreEqual("JobsView", itemsBinding.Path.Path);

            var selectedBinding = BindingOperations.GetBinding(grid, DataGrid.SelectedItemProperty);
            Assert.IsNotNull(selectedBinding);
            Assert.AreEqual("SelectedJob", selectedBinding.Path.Path);
        }
        finally
        {
            window.Close();
        }
    }

    [TestMethod]
    public void ReportedStatusCellTemplate_BindsTheActivitySpinnerToActiveState()
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
            var template = window.FindResource("ReportedStatusCellTemplate") as DataTemplate;
            Assert.IsNotNull(template, "The reported-status cell must keep a shared testable template.");

            var content = (FrameworkElement)template.LoadContent();
            var spinner = FindDescendant<ContentControl>(content);
            Assert.IsNotNull(spinner, "The cell must contain the small activity spinner.");
            Assert.IsNotNull(window.FindResource("ActivitySpinner") as Style);

            var binding = BindingOperations.GetBinding(spinner, UIElement.VisibilityProperty);
            Assert.IsNotNull(binding);
            Assert.AreEqual("IsActive", binding.Path.Path);
        }
        finally
        {
            window.Close();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match)
            {
                return match;
            }

            if (child is DependencyObject dependencyObject &&
                FindDescendant<T>(dependencyObject) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    [TestMethod]
    public void RightPanel_RetainsOriginalElapsedAndProjectPathBindings()
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
            var elapsed = window.FindName("SelectedJobElapsedText") as TextBlock;
            Assert.IsNotNull(elapsed, "The right-panel elapsed value must remain present.");
            var elapsedBinding = BindingOperations.GetBinding(elapsed, TextBlock.TextProperty);
            Assert.IsNotNull(elapsedBinding);
            Assert.AreEqual("SelectedJob.ElapsedText", elapsedBinding.Path.Path);

            var label = window.FindName("SelectedJobElapsedLabel") as TextBlock;
            Assert.IsNotNull(label);
            Assert.AreEqual("elapsed / observed span", label.Text);

            var projectPath = window.FindName("SelectedJobProjectPath") as TextBox;
            Assert.IsNotNull(projectPath, "The details project path must remain present.");
            Assert.IsTrue(projectPath.IsReadOnly, "Overview values must remain selectable without being editable.");
            var projectBinding = BindingOperations.GetBinding(projectPath, TextBox.TextProperty);
            Assert.IsNotNull(projectBinding);
            Assert.AreEqual("ProjectTooltip", projectBinding.Path.Path);
        }
        finally
        {
            window.Close();
        }
    }
}
