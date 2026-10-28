/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.Theming;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Views.Dialogs;

/// <summary>
/// Searchable dialog used to create a saved tool entry in a single surface.
/// </summary>
public partial class ToolPickerDialog : Window
{
    private sealed record ToolPickerOption(
        ToolDescriptor Descriptor,
        string DisplayName,
        string CategoryName,
        string Description,
        string AliasText,
        Brush CategoryBrush,
        Geometry? IconGeometry)
    {
        public string SearchableText => string.Join(
            " ",
            DisplayName,
            CategoryName,
            Description,
            AliasText,
            Descriptor.Id);
    }

    private readonly LocalizationManager _localizer;
    private readonly List<ToolPickerOption> _allOptions;
    private readonly ObservableCollection<ToolPickerOption> _filteredOptions = [];
    private readonly string _initialHost;
    private ToolPickerOption? _selectedOption;
    private string _lastAutoDisplayName = string.Empty;
    private bool _displayNameEdited;
    private bool _suppressDisplayNameChange;

    public ToolDescriptor? SelectedTool => _selectedOption?.Descriptor;
    public string SelectedDisplayName => DisplayNameTextBox.Text.Trim();
    public string SelectedHost => HostTextBox.Text.Trim();

    public ToolPickerDialog(
        LocalizationManager localizer,
        IEnumerable<ToolDescriptor> descriptors,
        string? group = null,
        string? initialHost = null)
    {
        _localizer = localizer;
        _initialHost = initialHost?.Trim() ?? string.Empty;
        InitializeComponent();
        WindowThemeHelper.ApplyCurrentTheme(this);

        _allOptions = descriptors
            .Select(CreateOption)
            .OrderBy(option => option.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ToolListBox.ItemsSource = _filteredOptions;

        Title = _localizer["AddToolDialogTitle"];
        HeaderText.Text = _localizer["AddToolDialogTitle"];
        SearchTextBox.Tag = _localizer["ToolsTabSearchPlaceholder"];
        NoResultsTitleText.Text = _localizer["ToolsNoResults"];
        NoResultsHintText.Text = _localizer["ToolsNoResultsHint"];
        GroupLabelText.Text = _localizer["ServerFieldGroup"];
        DisplayNameLabelText.Text = _localizer["AddToolDialogName"];
        HostLabelText.Text = _localizer["AddToolDialogHost"];
        HostTextBox.Tag = _localizer["ToolWatermarkHostnameOrIp"];
        CancelBtn.Content = _localizer["BtnCancel"];
        AddBtn.Content = _localizer["BtnAdd"];

        System.Windows.Automation.AutomationProperties.SetName(SearchTextBox, _localizer["ToolsTabSearchPlaceholder"]);
        System.Windows.Automation.AutomationProperties.SetName(ToolListBox, _localizer["AddMenuTool"]);
        System.Windows.Automation.AutomationProperties.SetName(DisplayNameTextBox, _localizer["AddToolDialogName"]);
        System.Windows.Automation.AutomationProperties.SetName(HostTextBox, _localizer["AddToolDialogHost"]);
        System.Windows.Automation.AutomationProperties.SetName(CancelBtn, _localizer["BtnCancel"]);
        System.Windows.Automation.AutomationProperties.SetName(AddBtn, _localizer["BtnAdd"]);

        if (!string.IsNullOrWhiteSpace(group))
        {
            GroupPanel.Visibility = Visibility.Visible;
            GroupValueText.Text = group;
        }

        RefreshFilter();

        Loaded += (_, _) =>
        {
            SearchTextBox.Focus();
            if (ToolListBox.SelectedItem is null && _filteredOptions.Count > 0)
            {
                ToolListBox.SelectedIndex = 0;
            }
        };
    }

    private ToolPickerOption CreateOption(ToolDescriptor descriptor)
    {
        var label = descriptor.ResolveLabel(_localizer.GetString);
        var category = _localizer[descriptor.CategoryLabelKey];
        var descKey = $"ToolDesc{descriptor.Id}";
        var description = _localizer[descKey];
        if (string.Equals(description, descKey, StringComparison.Ordinal))
        {
            description = string.Empty;
        }

        var brushKey = ToolRegistry.GetCategoryBrushKey(descriptor.Id);
        var categoryBrush = Application.Current.TryFindResource(brushKey) as Brush
            ?? Brushes.Gray;
        var iconGeometry = descriptor.IconResourceKey is not null
            ? Application.Current.TryFindResource(descriptor.IconResourceKey) as Geometry
            : null;

        return new ToolPickerOption(
            descriptor,
            label,
            category,
            description,
            string.Join(" ", descriptor.CommandPrefixes),
            categoryBrush,
            iconGeometry);
    }

    private void RefreshFilter()
    {
        var previousId = _selectedOption?.Descriptor.Id;
        var query = SearchTextBox.Text.Trim();

        var matches = string.IsNullOrWhiteSpace(query)
            ? _allOptions
            : _allOptions
                .Where(option => option.SearchableText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _filteredOptions.Clear();
        foreach (var option in matches)
        {
            _filteredOptions.Add(option);
        }

        NoResultsPanel.Visibility = _filteredOptions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var nextSelection = _filteredOptions.FirstOrDefault(option =>
                                string.Equals(option.Descriptor.Id, previousId, StringComparison.OrdinalIgnoreCase))
                            ?? _filteredOptions.FirstOrDefault();
        ToolListBox.SelectedItem = nextSelection;
        UpdateSelectionState(nextSelection);
    }

    private void UpdateSelectionState(ToolPickerOption? option)
    {
        _selectedOption = option;

        if (option is null)
        {
            SelectedToolIcon.Data = null;
            SelectedToolNameText.Text = string.Empty;
            SelectedToolCategoryText.Text = string.Empty;
            SelectedToolDescriptionText.Text = string.Empty;
            HostPanel.Visibility = Visibility.Collapsed;
            AddBtn.IsEnabled = false;
            return;
        }

        SelectedToolIcon.Data = option.IconGeometry;
        SelectedToolIcon.Fill = option.CategoryBrush;
        SelectedToolNameText.Text = option.DisplayName;
        SelectedToolCategoryText.Text = option.CategoryName;
        SelectedToolCategoryText.Foreground = option.CategoryBrush;
        SelectedToolDescriptionText.Text = option.Description;

        var nextDisplayName = option.DisplayName;
        if (!_displayNameEdited
            || string.IsNullOrWhiteSpace(DisplayNameTextBox.Text)
            || string.Equals(DisplayNameTextBox.Text, _lastAutoDisplayName, StringComparison.Ordinal))
        {
            _suppressDisplayNameChange = true;
            DisplayNameTextBox.Text = nextDisplayName;
            _suppressDisplayNameChange = false;
        }
        _lastAutoDisplayName = nextDisplayName;

        HostPanel.Visibility = option.Descriptor.IsNetworkTool
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (option.Descriptor.IsNetworkTool && string.IsNullOrWhiteSpace(HostTextBox.Text))
        {
            HostTextBox.Text = _initialHost;
        }

        AddBtn.IsEnabled = true;
    }

    private bool TrySubmit()
    {
        ValidationText.Visibility = Visibility.Collapsed;
        ValidationText.Text = string.Empty;

        if (_selectedOption is null)
        {
            ShowValidation(_localizer["AddToolPickerValidationTool"]);
            ToolListBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text))
        {
            ShowValidation(_localizer["AddToolPickerValidationName"]);
            DisplayNameTextBox.Focus();
            return false;
        }

        var host = HostTextBox.Text.Trim();
        if (_selectedOption?.Descriptor.IsNetworkTool == true
            && !string.IsNullOrEmpty(host)
            && !InputValidator.Validate(host, "Address"))
        {
            ShowValidation(_localizer["ErrorInvalidHost"]);
            HostTextBox.Focus();
            return false;
        }

        DialogResult = true;
        return true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => RefreshFilter();

    private void OnToolSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionState(ToolListBox.SelectedItem as ToolPickerOption);

    private void OnDisplayNameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressDisplayNameChange)
        {
            _displayNameEdited = true;
        }
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _filteredOptions.Count > 0)
        {
            ToolListBox.Focus();
            ToolListBox.SelectedIndex = Math.Max(0, ToolListBox.SelectedIndex);
            e.Handled = true;
        }
    }

    private void OnToolListDoubleClick(object sender, MouseButtonEventArgs e)
        => _ = TrySubmit();

    private void OnAddClick(object sender, RoutedEventArgs e)
        => _ = TrySubmit();
}
