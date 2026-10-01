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
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Matching;

namespace Heimdall.App.ViewModels.Tools;

public sealed partial class RegexTesterViewModel : ObservableObject, IDisposable
{
    public const int MaxDisplayedMatches = 500;
    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly IRegexTesterToolService _service;
    private readonly DispatcherTimer _debounceTimer;
    private LocalizationManager? _localizer;
    private bool _initialized;
    private bool _disposed;
    private RegexStatusKind _lastStatusKind = RegexStatusKind.None;
    private string _lastStatusMessage = string.Empty;
    private int _lastTotalMatchCount;

    [ObservableProperty] private string _patternText = string.Empty;
    [ObservableProperty] private string _testText = string.Empty;
    [ObservableProperty] private bool _isIgnoreCaseChecked;
    [ObservableProperty] private bool _isMultilineChecked;
    [ObservableProperty] private bool _isSinglelineChecked;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusForegroundBrushKey = "TextSecondaryBrush";
    [ObservableProperty] private string _matchCountText = string.Empty;
    [ObservableProperty] private string _matchesCopyText = string.Empty;
    [ObservableProperty] private bool _isEmptyStateVisible = true;
    [ObservableProperty] private bool _isResultsPanelVisible;
    [ObservableProperty] private bool _isTruncatedNoticeVisible;
    [ObservableProperty] private string _truncatedNoticeText = string.Empty;
    [ObservableProperty] private IReadOnlyList<RegexHighlightSegment> _highlightSegments = Array.Empty<RegexHighlightSegment>();

    public ObservableCollection<MatchDisplayItem> DisplayedMatches { get; } = [];

    public RegexTesterViewModel(IRegexTesterToolService? service = null)
    {
        _service = service ?? new RegexTesterToolService();
        _debounceTimer = new DispatcherTimer { Interval = DebounceDelay };
        _debounceTimer.Tick += OnDebounceTick;
    }

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

        RefreshLocalizedState();
    }

    public void PrefillPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        PatternText = pattern;
    }

    public void MarkInitialized() => _initialized = true;

    internal void FlushPendingMatch()
    {
        _debounceTimer.Stop();
        ExecuteMatch();
    }

    partial void OnPatternTextChanged(string value)
    {
        if (_initialized) { ScheduleMatch(); }
    }

    partial void OnTestTextChanged(string value)
    {
        if (_initialized) { ScheduleMatch(); }
    }

    partial void OnIsIgnoreCaseCheckedChanged(bool value)
    {
        if (_initialized) { ScheduleMatch(); }
    }

    partial void OnIsMultilineCheckedChanged(bool value)
    {
        if (_initialized) { ScheduleMatch(); }
    }

    partial void OnIsSinglelineCheckedChanged(bool value)
    {
        if (_initialized) { ScheduleMatch(); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        GC.SuppressFinalize(this);
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        ExecuteMatch();
    }

    private void ScheduleMatch()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void ExecuteMatch()
    {
        ClearDisplayedState();

        if (string.IsNullOrEmpty(PatternText))
        {
            SetStatus(RegexStatusKind.None);
            IsEmptyStateVisible = true;
            IsResultsPanelVisible = false;
            return;
        }

        var result = _service.Test(PatternText, TestText, IsIgnoreCaseChecked, IsMultilineChecked, IsSinglelineChecked);
        switch (result.Status)
        {
            case RegexTestStatus.EmptyPattern:
                SetStatus(RegexStatusKind.None);
                IsEmptyStateVisible = true;
                IsResultsPanelVisible = false;
                break;
            case RegexTestStatus.InvalidPattern:
                SetStatus(RegexStatusKind.Invalid, result.ErrorMessage);
                IsEmptyStateVisible = false;
                IsResultsPanelVisible = false;
                break;
            case RegexTestStatus.MatchTimeout:
                SetStatus(RegexStatusKind.Timeout);
                IsEmptyStateVisible = false;
                IsResultsPanelVisible = false;
                break;
            case RegexTestStatus.Success:
                SetStatus(RegexStatusKind.Valid);
                if (string.IsNullOrEmpty(TestText))
                {
                    IsEmptyStateVisible = false;
                    IsResultsPanelVisible = false;
                    break;
                }

                ApplySuccessfulMatch(result);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void ApplySuccessfulMatch(RegexTestResult result)
    {
        _lastTotalMatchCount = result.TotalMatchCount;
        MatchCountText = FormatMatchCount(result.TotalMatchCount);
        IsEmptyStateVisible = false;
        IsResultsPanelVisible = true;

        var displayCount = Math.Min(result.TotalMatchCount, MaxDisplayedMatches);
        for (var i = 0; i < displayCount; i++)
        {
            var item = new MatchDisplayItem(i, result.Matches[i]);
            item.ApplyLocalization(_localizer);
            DisplayedMatches.Add(item);
        }

        if (result.TotalMatchCount > MaxDisplayedMatches)
        {
            IsTruncatedNoticeVisible = true;
            TruncatedNoticeText = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolRegexMatchesTruncated"),
                MaxDisplayedMatches,
                result.TotalMatchCount);
        }

        HighlightSegments = BuildHighlightSegments(TestText, result.Matches);
        UpdateMatchesCopyText();
    }

    private static IReadOnlyList<RegexHighlightSegment> BuildHighlightSegments(string input, IReadOnlyList<RegexMatchInfo> matches)
    {
        if (matches.Count == 0)
        {
            return Array.Empty<RegexHighlightSegment>();
        }

        var segments = new List<RegexHighlightSegment>();
        var lastEnd = 0;
        foreach (var match in matches)
        {
            if (match.Index > lastEnd)
            {
                segments.Add(new RegexHighlightSegment(input[lastEnd..match.Index], RegexHighlightKind.Normal));
            }

            var kind = match.Groups.Any(group => group.IsNamed && group.Length > 0)
                ? RegexHighlightKind.NamedGroupMatch
                : RegexHighlightKind.Match;
            segments.Add(new RegexHighlightSegment(match.Value, kind));
            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < input.Length)
        {
            segments.Add(new RegexHighlightSegment(input[lastEnd..], RegexHighlightKind.Normal));
        }

        return segments;
    }

    private void ClearDisplayedState()
    {
        DisplayedMatches.Clear();
        MatchCountText = string.Empty;
        MatchesCopyText = string.Empty;
        HighlightSegments = Array.Empty<RegexHighlightSegment>();
        IsTruncatedNoticeVisible = false;
        TruncatedNoticeText = string.Empty;
        _lastTotalMatchCount = 0;
    }

    private void UpdateMatchesCopyText()
    {
        var builder = new StringBuilder();
        foreach (var item in DisplayedMatches)
        {
            builder.AppendLine(item.DisplayText);
        }

        if (IsTruncatedNoticeVisible && !string.IsNullOrEmpty(TruncatedNoticeText))
        {
            builder.AppendLine(TruncatedNoticeText);
        }

        MatchesCopyText = builder.ToString();
    }

    private void SetStatus(RegexStatusKind kind, string? message = null)
    {
        _lastStatusKind = kind;
        _lastStatusMessage = message ?? string.Empty;
        RefreshStatusText();
    }

    private void RefreshLocalizedState()
    {
        RefreshStatusText();
        if (IsResultsPanelVisible)
        {
            MatchCountText = FormatMatchCount(_lastTotalMatchCount);
        }

        if (IsTruncatedNoticeVisible)
        {
            TruncatedNoticeText = string.Format(
                CultureInfo.CurrentCulture,
                L("ToolRegexMatchesTruncated"),
                MaxDisplayedMatches,
                _lastTotalMatchCount);
        }

        foreach (var item in DisplayedMatches)
        {
            item.ApplyLocalization(_localizer);
        }

        UpdateMatchesCopyText();
    }

    private void RefreshStatusText()
    {
        switch (_lastStatusKind)
        {
            case RegexStatusKind.None:
                StatusText = string.Empty;
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case RegexStatusKind.Valid:
                StatusText = L("ToolRegexStatusValid");
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case RegexStatusKind.Invalid:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolRegexStatusInvalid"), _lastStatusMessage);
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            case RegexStatusKind.Timeout:
                StatusText = L("ToolRegexStatusTimeout");
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void OnLocaleChanged(string _) => RefreshLocalizedState();

    private string L(string key) => _localizer?[key] ?? key;

    private string FormatMatchCount(int count) =>
        _localizer?.FormatCount(count, "ToolRegexMatchCountOne", "ToolRegexMatchCount", count) ?? "ToolRegexMatchCount";

    private enum RegexStatusKind
    {
        None,
        Valid,
        Invalid,
        Timeout,
    }
}

public enum RegexHighlightKind
{
    Normal,
    Match,
    NamedGroupMatch,
}

public readonly record struct RegexHighlightSegment(string Text, RegexHighlightKind Kind);

public sealed partial class MatchDisplayItem : ObservableObject
{
    private readonly int _matchDisplayIndex;
    private readonly RegexMatchInfo _matchInfo;

    [ObservableProperty] private string _displayText = string.Empty;

    public MatchDisplayItem(int matchDisplayIndex, RegexMatchInfo matchInfo)
    {
        _matchDisplayIndex = matchDisplayIndex;
        _matchInfo = matchInfo;
    }

    public void ApplyLocalization(LocalizationManager? localizer)
    {
        var builder = new StringBuilder();
        builder.AppendFormat(
            CultureInfo.CurrentCulture,
            localizer?["ToolRegexMatchEntry"] ?? "ToolRegexMatchEntry",
            _matchDisplayIndex,
            _matchInfo.Index,
            _matchInfo.Value);

        foreach (var group in _matchInfo.Groups)
        {
            builder.AppendFormat(
                CultureInfo.CurrentCulture,
                localizer?["ToolRegexGroupEntry"] ?? "ToolRegexGroupEntry",
                group.Index,
                group.Value);
        }

        DisplayText = builder.ToString();
    }
}
