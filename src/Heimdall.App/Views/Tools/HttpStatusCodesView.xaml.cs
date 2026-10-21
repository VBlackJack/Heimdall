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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// HTTP status codes reference tool with search/filter and grouped display.
/// Click any entry to copy the code and name to clipboard.
/// </summary>
public partial class HttpStatusCodesView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private List<HttpStatusEntry> _allEntries = [];
    private ICollectionView? _collectionView;

    public HttpStatusCodesView()
    {
        InitializeComponent();
        TxtFilter.TextChanged += OnFilterTextChanged;
        StatusListView.MouseDoubleClick += OnItemDoubleClick;
        StatusListView.KeyDown += OnListKeyDown;
    }

    /// <summary>
    /// Initializes the tool with optional context and localization.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        ApplyLocalization();
        BuildStatusCodes();
        ApplyGrouping();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtFilter.Focus();
            TxtFilter.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolHttpTitle");
        TxtFilter.Tag = L("ToolHttpFilterPlaceholder");
        TxtCopyHint.Text = L("ToolHttpCopyHint");

        System.Windows.Automation.AutomationProperties.SetName(TxtFilter, L("ToolHttpFilterPlaceholder"));
        System.Windows.Automation.AutomationProperties.SetName(StatusListView, L("ToolHttpTitle"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        TxtNoResults.Text = L("ToolHttpNoResults");
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
    }

    private void BuildStatusCodes()
    {
        _allEntries =
        [
            // 1xx Informational
            E(100, "Continue", L("ToolHttp100")),
            E(101, "Switching Protocols", L("ToolHttp101")),
            E(102, "Processing", L("ToolHttp102")),
            E(103, "Early Hints", L("ToolHttp103")),

            // 2xx Success
            E(200, "OK", L("ToolHttp200")),
            E(201, "Created", L("ToolHttp201")),
            E(202, "Accepted", L("ToolHttp202")),
            E(203, "Non-Authoritative Information", L("ToolHttp203")),
            E(204, "No Content", L("ToolHttp204")),
            E(205, "Reset Content", L("ToolHttp205")),
            E(206, "Partial Content", L("ToolHttp206")),
            E(207, "Multi-Status", L("ToolHttp207")),
            E(208, "Already Reported", L("ToolHttp208")),
            E(226, "IM Used", L("ToolHttp226")),

            // 3xx Redirection
            E(300, "Multiple Choices", L("ToolHttp300")),
            E(301, "Moved Permanently", L("ToolHttp301")),
            E(302, "Found", L("ToolHttp302")),
            E(303, "See Other", L("ToolHttp303")),
            E(304, "Not Modified", L("ToolHttp304")),
            E(305, "Use Proxy", L("ToolHttp305")),
            E(307, "Temporary Redirect", L("ToolHttp307")),
            E(308, "Permanent Redirect", L("ToolHttp308")),

            // 4xx Client Error
            E(400, "Bad Request", L("ToolHttp400")),
            E(401, "Unauthorized", L("ToolHttp401")),
            E(402, "Payment Required", L("ToolHttp402")),
            E(403, "Forbidden", L("ToolHttp403")),
            E(404, "Not Found", L("ToolHttp404")),
            E(405, "Method Not Allowed", L("ToolHttp405")),
            E(406, "Not Acceptable", L("ToolHttp406")),
            E(407, "Proxy Authentication Required", L("ToolHttp407")),
            E(408, "Request Timeout", L("ToolHttp408")),
            E(409, "Conflict", L("ToolHttp409")),
            E(410, "Gone", L("ToolHttp410")),
            E(411, "Length Required", L("ToolHttp411")),
            E(412, "Precondition Failed", L("ToolHttp412")),
            E(413, "Content Too Large", L("ToolHttp413")),
            E(414, "URI Too Long", L("ToolHttp414")),
            E(415, "Unsupported Media Type", L("ToolHttp415")),
            E(416, "Range Not Satisfiable", L("ToolHttp416")),
            E(417, "Expectation Failed", L("ToolHttp417")),
            E(418, "I'm a Teapot", L("ToolHttp418")),
            E(421, "Misdirected Request", L("ToolHttp421")),
            E(422, "Unprocessable Content", L("ToolHttp422")),
            E(423, "Locked", L("ToolHttp423")),
            E(424, "Failed Dependency", L("ToolHttp424")),
            E(425, "Too Early", L("ToolHttp425")),
            E(426, "Upgrade Required", L("ToolHttp426")),
            E(428, "Precondition Required", L("ToolHttp428")),
            E(429, "Too Many Requests", L("ToolHttp429")),
            E(431, "Request Header Fields Too Large", L("ToolHttp431")),
            E(451, "Unavailable For Legal Reasons", L("ToolHttp451")),

            // 5xx Server Error
            E(500, "Internal Server Error", L("ToolHttp500")),
            E(501, "Not Implemented", L("ToolHttp501")),
            E(502, "Bad Gateway", L("ToolHttp502")),
            E(503, "Service Unavailable", L("ToolHttp503")),
            E(504, "Gateway Timeout", L("ToolHttp504")),
            E(505, "HTTP Version Not Supported", L("ToolHttp505")),
            E(506, "Variant Also Negotiates", L("ToolHttp506")),
            E(507, "Insufficient Storage", L("ToolHttp507")),
            E(508, "Loop Detected", L("ToolHttp508")),
            E(510, "Not Extended", L("ToolHttp510")),
            E(511, "Network Authentication Required", L("ToolHttp511")),
        ];

        StatusListView.ItemsSource = _allEntries;
    }

    private void ApplyGrouping()
    {
        _collectionView = CollectionViewSource.GetDefaultView(_allEntries);
        _collectionView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(HttpStatusEntry.Category)));
        _collectionView.Filter = FilterPredicate;
        StatusListView.ItemsSource = _collectionView;
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not HttpStatusEntry entry)
        {
            return false;
        }

        var filter = TxtFilter.Text.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        return entry.Code.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Description.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        _collectionView?.Refresh();
        var visibleCount = _collectionView?.Cast<object>().Count() ?? 0;
        TxtNoResults.Visibility = visibleCount == 0 && !string.IsNullOrEmpty(TxtFilter.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CopySelectedEntry();
    }

    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.C &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            CopySelectedEntry();
            e.Handled = true;
        }
    }

    private void CopySelectedEntry()
    {
        if (StatusListView.SelectedItem is HttpStatusEntry entry)
        {
            try { Clipboard.SetText($"{entry.Code} {entry.Name}"); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard locked */ }
        }
    }

    private HttpStatusEntry E(int code, string name, string description)
    {
        var category = code switch
        {
            < 200 => L("ToolHttpCat1xx"),
            < 300 => L("ToolHttpCat2xx"),
            < 400 => L("ToolHttpCat3xx"),
            < 500 => L("ToolHttpCat4xx"),
            _ => L("ToolHttpCat5xx")
        };

        var brush = code switch
        {
            < 200 => TryGetBrush("TextSecondaryBrush", Brushes.Gray),
            < 300 => TryGetBrush("SuccessBrush", Brushes.Green),
            < 400 => TryGetBrush("AccentTextBrush", Brushes.DodgerBlue),
            < 500 => TryGetBrush("WarningBrush", Brushes.Orange),
            _ => TryGetBrush("ErrorBrush", Brushes.Red)
        };

        return new HttpStatusEntry(code, name, description, category, brush);
    }

    private static Brush TryGetBrush(string key, Brush fallback)
    {
        return Application.Current.TryFindResource(key) as Brush ?? fallback;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpHTTP").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        TxtFilter.TextChanged -= OnFilterTextChanged;
        StatusListView.MouseDoubleClick -= OnItemDoubleClick;
        StatusListView.KeyDown -= OnListKeyDown;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Data record for a single HTTP status code entry.
    /// </summary>
    internal sealed record HttpStatusEntry(
        int Code,
        string Name,
        string Description,
        string Category,
        Brush CategoryBrush);
}
