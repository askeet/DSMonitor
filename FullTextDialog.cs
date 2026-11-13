using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace DSMonitor;

internal static class FullTextDialog
{
    private const double MinimumFontSize = 12;

    public static bool? Show(Window owner, string title, string text, double initialFontSize)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var dialog = Create(title, text, initialFontSize, Clipboard.SetText);
        dialog.Owner = owner;
        return dialog.ShowDialog();
    }

    internal static Window Create(
        string title,
        string text,
        double initialFontSize,
        Action<string> copyAll)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(copyAll);

        var dialog = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 1000,
            Height = 720,
            MinWidth = 560,
            MinHeight = 360,
            ResizeMode = ResizeMode.CanResize,
            Background = new SolidColorBrush(Color.FromRgb(17, 21, 28)),
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            ShowInTaskbar = false
        };
        NameScope.SetNameScope(dialog, new NameScope());

        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sectionTitle = new TextBlock
        {
            Name = "FullTextDialogTitle",
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            Margin = new Thickness(0, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Center
        };
        dialog.RegisterName(sectionTitle.Name, sectionTitle);
        System.Windows.Automation.AutomationProperties.SetName(sectionTitle, title);
        Grid.SetRow(sectionTitle, 0);
        layout.Children.Add(sectionTitle);

        var textBox = new TextBox
        {
            Name = "FullTextDialogTextBox",
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = new SolidColorBrush(Color.FromRgb(15, 20, 27)),
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 88)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = Math.Max(MinimumFontSize, initialFontSize)
        };
        dialog.RegisterName(textBox.Name, textBox);
        System.Windows.Automation.AutomationProperties.SetName(textBox, $"{title} full text");
        Grid.SetRow(textBox, 1);
        layout.Children.Add(textBox);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var fontPicker = new ComboBox
        {
            Name = "FullTextDialogFontPicker",
            Width = 90,
            ItemsSource = new[] { 12d, 14d, 16d, 18d, 20d, 24d },
            SelectedItem = Math.Max(MinimumFontSize, initialFontSize),
            Background = new SolidColorBrush(Color.FromRgb(29, 36, 47)),
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 88)),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Text size",
            Template = CreateFontPickerTemplate(),
            ItemContainerStyle = CreateFontPickerItemStyle()
        };
        dialog.RegisterName(fontPicker.Name, fontPicker);
        fontPicker.SelectionChanged += (_, _) =>
        {
            if (fontPicker.SelectedItem is double size)
            {
                textBox.FontSize = Math.Max(MinimumFontSize, size);
            }
        };
        DockPanel.SetDock(fontPicker, Dock.Left);
        footer.Children.Add(fontPicker);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var copyButton = CreateButton("Copy All");
        copyButton.Name = "FullTextDialogCopyAllButton";
        copyButton.IsEnabled = text.Length > 0;
        dialog.RegisterName(copyButton.Name, copyButton);
        copyButton.Click += (_, _) => copyAll(text);
        var closeButton = CreateButton("Close");
        closeButton.Margin = new Thickness(8, 0, 0, 0);
        closeButton.IsDefault = true;
        closeButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        dialog.Content = layout;
        return dialog;
    }

    private static Button CreateButton(string text) => new()
    {
        Content = text,
        MinWidth = 88,
        Padding = new Thickness(12, 7, 12, 7),
        Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
        Background = new SolidColorBrush(Color.FromRgb(29, 36, 47)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 88)),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand,
        Style = CreateButtonStyle()
    };

    private static Style CreateFontPickerItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "FontPickerItemBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };
        var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 61, 82)), "FontPickerItemBorder"));
        template.Triggers.Add(selected);
        var highlighted = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(53, 67, 86)), "FontPickerItemBorder"));
        template.Triggers.Add(highlighted);

        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(29, 36, 47))));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private static ControlTemplate CreateFontPickerTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "DropDownToggle";
        toggle.SetValue(FrameworkElement.FocusableProperty, false);
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ComboBox.IsDropDownOpen))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Mode = BindingMode.TwoWay
        });
        root.AppendChild(toggle);

        var selectionBorder = new FrameworkElementFactory(typeof(Border));
        selectionBorder.SetValue(UIElement.IsHitTestVisibleProperty, false);
        selectionBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        selectionBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        selectionBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        selectionBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        root.AppendChild(selectionBorder);

        var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter));
        selectedContent.SetValue(UIElement.IsHitTestVisibleProperty, false);
        selectedContent.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        selectedContent.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        selectedContent.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        selectedContent.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 26, 0));
        selectedContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(selectedContent);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
        arrow.SetValue(TextBlock.TextProperty, "▾");
        arrow.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(226, 232, 240)));
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        root.AppendChild(arrow);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetValue(Popup.StaysOpenProperty, false);
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ComboBox.IsDropDownOpen))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Mode = BindingMode.TwoWay
        });
        popup.SetBinding(Popup.PlacementTargetProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(29, 36, 47)));
        popupBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(58, 70, 88)));
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scrollViewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scrollViewer.SetValue(FrameworkElement.MaxHeightProperty, 240d);
        scrollViewer.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        popupBorder.AppendChild(scrollViewer);
        popup.AppendChild(popupBorder);
        root.AppendChild(popup);

        return new ControlTemplate(typeof(ComboBox)) { VisualTree = root };
    }

    private static Style CreateButtonStyle()
    {
        var style = new Style(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 55, 72)), "ButtonBorder"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 72, 94)), "ButtonBorder"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
