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
            Assert.IsTrue(grid.Columns[1].Width.DisplayValue <= 100);
            Assert.IsTrue(grid.Columns[2].Width.DisplayValue <= 80);
            Assert.IsTrue(grid.Columns[3].Width.DisplayValue <= 120);
            Assert.IsTrue(grid.Columns[4].Width.DisplayValue <= 150);

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

            var projectPath = window.FindName("SelectedJobProjectPath") as TextBlock;
            Assert.IsNotNull(projectPath, "The details project path must remain present.");
            var projectBinding = BindingOperations.GetBinding(projectPath, TextBlock.TextProperty);
            Assert.IsNotNull(projectBinding);
            Assert.AreEqual("ProjectTooltip", projectBinding.Path.Path);
        }
        finally
        {
            window.Close();
        }
    }
}
