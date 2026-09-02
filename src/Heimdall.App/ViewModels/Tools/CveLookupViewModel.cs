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
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// Search states for the CVE lookup UI.
/// </summary>
public enum CveSearchState
{
    Empty,
    NoResults,
    HasResults,
}

/// <summary>
/// Locale-aware projection of a core CVE match for the WPF view.
/// </summary>
public sealed record CveMatchDisplayItem(
    string CveId,
    double CvssScore,
    CveSeverity Severity,
    string SeverityLabel,
    string CvssLabel,
    string Summary,
    string AffectedLabel,
    string AffectedVersions);

/// <summary>
/// ViewModel for the offline CVE lookup tool.
/// </summary>
public sealed partial class CveLookupViewModel : ObservableObject, IDisposable
{
    private LocalizationManager? _localizer;
    private CveSearchResult _lastResult = new(string.Empty, Array.Empty<CveMatch>());
    private readonly ObservableCollection<CveMatchDisplayItem> _results = [];
    private bool _disposed;

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private CveSearchState _state = CveSearchState.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _dbInfoText = string.Empty;
    [ObservableProperty] private string _helpText = string.Empty;

    // No service layer: the engine is pure, sync, offline - direct static call
    // into CveLookupEngine is the simplest honest answer for this tool.
    public CveLookupViewModel()
    {
    }

    /// <summary>
    /// Gets the projected CVE results for binding.
    /// </summary>
    public ObservableCollection<CveMatchDisplayItem> Results => _results;

    /// <summary>
    /// Initializes or updates localization state.
    /// </summary>
    public void Initialize(LocalizationManager? localizer)
    {
        if (ReferenceEquals(_localizer, localizer))
        {
            return;
        }

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        _localizer = localizer;

        if (_localizer is not null)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
        }

        Reproject();
        RefreshLabels();
    }

    /// <summary>
    /// Prefills the input and immediately triggers a search.
    /// </summary>
    public void SearchWith(string text)
    {
        Input = text;
        SearchCommand.Execute(null);
    }

    [RelayCommand]
    private void Search()
    {
        var trimmed = Input?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            State = CveSearchState.Empty;
            _lastResult = new CveSearchResult(string.Empty, Array.Empty<CveMatch>());
            _results.Clear();
            SummaryText = string.Empty;
            CopyCommand.NotifyCanExecuteChanged();
            return;
        }

        _lastResult = CveLookupEngine.Search(trimmed);
        Reproject();
        State = _lastResult.Matches.Count == 0 ? CveSearchState.NoResults : CveSearchState.HasResults;
        CopyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        var text = CveLookupEngine.BuildCopyText(_lastResult, L);

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked by another process.
        }
    }

    [RelayCommand]
    private void ToggleHelp()
    {
        IsHelpVisible = !IsHelpVisible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        GC.SuppressFinalize(this);
    }

    private bool CanCopy() => _results.Count > 0;

    private void OnLocaleChanged(string _)
    {
        Reproject();
        RefreshLabels();
    }

    private void Reproject()
    {
        _results.Clear();

        var affectedLabel = L("ToolCveColAffected") + ": ";
        foreach (var match in _lastResult.Matches)
        {
            _results.Add(new CveMatchDisplayItem(
                CveId: match.Id,
                CvssScore: match.CvssScore,
                Severity: match.Severity,
                SeverityLabel: L(SeverityKey(match.Severity)),
                CvssLabel: $"CVSS {match.CvssScore.ToString("F1", CultureInfo.InvariantCulture)}",
                Summary: match.Summary,
                AffectedLabel: affectedLabel,
                AffectedVersions: match.AffectedVersions));
        }

        SummaryText = _lastResult.Matches.Count > 0
            ? string.Format(L("ToolCveSummary"), _lastResult.Matches.Count, _lastResult.ResolvedQuery)
            : string.Empty;
        CopyCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLabels()
    {
        DbInfoText = string.Format(
            L("ToolCveDbInfo"),
            CveLookupEngine.TotalCveCount,
            CveLookupEngine.TotalProductCount);
        HelpText = L("ToolHelpCVELOOKUP").Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static string SeverityKey(CveSeverity severity) => severity switch
    {
        CveSeverity.Critical => "ToolCveSeverityCritical",
        CveSeverity.High => "ToolCveSeverityHigh",
        CveSeverity.Medium => "ToolCveSeverityMedium",
        CveSeverity.Low => "ToolCveSeverityLow",
        _ => "ToolCveSeverityLow",
    };

    private string L(string key) => _localizer?[key] ?? key;
}
