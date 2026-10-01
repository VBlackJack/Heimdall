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
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services.Import;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the .rdp import preview dialog.
/// </summary>
public partial class RdpImportDialogViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;
    private bool _syncingSelection;

    private readonly RdpImportDialogTextOptions _textOptions;

    public RdpImportDialogViewModel(
        LocalizationManager localizer,
        RdpImportPreview preview,
        RdpImportDialogTextOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(preview);

        _localizer = localizer;
        _textOptions = textOptions ?? RdpImportDialogTextOptions.RdpImport;
        Preview = preview;

        DialogTitle = _localizer[_textOptions.TitleKey];
        SubtitleText = _localizer.Format(
            _localizer.SelectCountKey(preview.Entries.Count, _textOptions.SubtitleOneKey, _textOptions.SubtitleKey),
            preview.Entries.Count);
        FileIssuesText = BuildFileIssuesText(preview);
        SelectAllText = _localizer["DialogImportRdpSelectAll"];
        SelectNoneText = _localizer["DialogImportRdpSelectNone"];
        ApplyToAllText = _localizer["DialogImportRdpApplyAllConflicts"];
        ApplyAllSkipText = _localizer["DialogImportRdpConflictSkip"];
        ApplyAllReplaceText = _localizer["DialogImportRdpConflictReplace"];
        ApplyAllAutoRenameText = _localizer["DialogImportRdpConflictAutoRename"];
        ConfirmText = _localizer[_textOptions.ConfirmKey];
        CancelText = _localizer["BtnCancel"];
        SourceColumnHeader = _localizer["DialogImportRdpColSource"];
        NameColumnHeader = _localizer["DialogImportRdpColName"];
        HostColumnHeader = _localizer["DialogImportRdpColHost"];
        StatusColumnHeader = _localizer["DialogImportRdpColStatus"];
        ConflictColumnHeader = _localizer["DialogImportRdpColConflict"];

        ConflictOptions =
        [
            new RdpConflictResolutionOption(RdpConflictResolution.Skip, _localizer["DialogImportRdpConflictSkip"]),
            new RdpConflictResolutionOption(RdpConflictResolution.Replace, _localizer["DialogImportRdpConflictReplace"]),
            new RdpConflictResolutionOption(RdpConflictResolution.AutoRename, _localizer["DialogImportRdpConflictAutoRename"]),
        ];

        Rows = new ObservableCollection<RdpImportRowViewModel>(
            preview.Entries.Select(entry => new RdpImportRowViewModel(entry, _localizer)));

        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        RefreshState();
    }

    public RdpImportPreview Preview { get; }

    public string DialogTitle { get; }

    public string SubtitleText { get; }

    public string? FileIssuesText { get; }

    public string SelectAllText { get; }

    public string SelectNoneText { get; }

    public string ApplyToAllText { get; }

    public string ApplyAllSkipText { get; }

    public string ApplyAllReplaceText { get; }

    public string ApplyAllAutoRenameText { get; }

    public string ConfirmText { get; }

    public string CancelText { get; }

    public string SourceColumnHeader { get; }

    public string NameColumnHeader { get; }

    public string HostColumnHeader { get; }

    public string StatusColumnHeader { get; }

    public string ConflictColumnHeader { get; }

    public ObservableCollection<RdpImportRowViewModel> Rows { get; }

    public IReadOnlyList<RdpConflictResolutionOption> ConflictOptions { get; }

    public bool HasFileIssues => !string.IsNullOrWhiteSpace(FileIssuesText);

    [ObservableProperty]
    private bool _allSelected;

    public int TotalSelectedCount => Rows.Count(row => row.IsSelected);

    public bool HasPasswordWarnings => _textOptions.IncludePasswordWarningsInSummary && Rows.Any(row => row.HasPasswordBlob);

    public bool HasParseErrors => Rows.Any(row => row.HasParseError);

    public bool CanConfirm => Rows.Any(row => row.IsSelected);

    public string SummaryText
    {
        get
        {
            int selected = TotalSelectedCount;
            int conflicts = Rows.Count(row => row.HasNameConflict);
            string selectedText = _localizer.Format(
                _localizer.SelectCountKey(selected, _textOptions.SelectedOneKey, _textOptions.SelectedKey),
                selected,
                Rows.Count);
            string conflictsText = _localizer.FormatCount(conflicts, "ImportCountConflictsOne", "ImportCountConflicts", conflicts);
            if (!_textOptions.IncludePasswordWarningsInSummary)
            {
                return _localizer.Format(_textOptions.SummaryKey, selectedText, conflictsText);
            }

            int passwordWarnings = Rows.Count(row => row.HasPasswordBlob);
            return _localizer.Format(
                _textOptions.SummaryKey,
                selectedText,
                conflictsText,
                _localizer.FormatCount(
                    passwordWarnings,
                    "ImportCountPasswordWarningsOne",
                    "ImportCountPasswordWarnings",
                    passwordWarnings));
        }
    }

    public RdpImportSelection? Result { get; private set; }

    public event Action? CloseRequested;

    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            foreach (var row in Rows.Where(row => !row.HasParseError))
            {
                row.IsSelected = value;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        RefreshState();
    }

    [RelayCommand]
    private void SelectAll()
    {
        AllSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        _syncingSelection = true;
        try
        {
            foreach (var row in Rows)
            {
                row.IsSelected = false;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        RefreshState();
    }

    [RelayCommand]
    private void ApplyAllSkip() => ApplyConflictResolutionToAll(RdpConflictResolution.Skip);

    [RelayCommand]
    private void ApplyAllReplace() => ApplyConflictResolutionToAll(RdpConflictResolution.Replace);

    [RelayCommand]
    private void ApplyAllAutoRename() => ApplyConflictResolutionToAll(RdpConflictResolution.AutoRename);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        Result = new RdpImportSelection
        {
            Entries =
            [
                .. Rows.Select(row => new RdpImportSelectionEntry
                {
                    SourceFilePath = row.SourceFilePath,
                    IsSelected = row.IsSelected,
                    ConflictResolution = row.ConflictResolution
                })
            ]
        };

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke();
    }

    private void ApplyConflictResolutionToAll(RdpConflictResolution resolution)
    {
        foreach (var row in Rows.Where(row => row.HasNameConflict))
        {
            row.ConflictResolution = resolution;
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpImportRowViewModel.IsSelected))
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        _syncingSelection = true;
        try
        {
            AllSelected = Rows.Count > 0 && Rows.Where(row => !row.HasParseError).All(row => row.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }

        ConfirmCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TotalSelectedCount));
        OnPropertyChanged(nameof(HasPasswordWarnings));
        OnPropertyChanged(nameof(HasParseErrors));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SummaryText));
    }

    private string? BuildFileIssuesText(RdpImportPreview preview)
    {
        var segments = new List<string>();

        if (preview.FilesNotFound.Count > 0)
        {
            segments.Add(_localizer.FormatCount(
                preview.FilesNotFound.Count,
                "DialogImportRdpFilesNotFoundOne",
                "DialogImportRdpFilesNotFound",
                preview.FilesNotFound.Count));
        }

        if (preview.FilesUnreadable.Count > 0)
        {
            segments.Add(_localizer.FormatCount(
                preview.FilesUnreadable.Count,
                "DialogImportRdpFilesUnreadableOne",
                "DialogImportRdpFilesUnreadable",
                preview.FilesUnreadable.Count));
        }

        return segments.Count == 0 ? null : string.Join(" ", segments);
    }
}

public partial class RdpImportRowViewModel : ObservableObject
{
    private readonly LocalizationManager _localizer;

    public RdpImportRowViewModel(RdpImportPreviewEntry previewEntry, LocalizationManager localizer)
    {
        PreviewEntry = previewEntry;
        _localizer = localizer;
        SourceFileName = Path.GetFileName(previewEntry.SourceFilePath);
        SourceFilePath = previewEntry.SourceFilePath;
        ProposedName = previewEntry.ProposedName;
        TargetHost = previewEntry.Candidate.RemotePort > 0
            ? $"{previewEntry.Candidate.RemoteServer}:{previewEntry.Candidate.RemotePort}"
            : previewEntry.Candidate.RemoteServer;
        HasPasswordBlob = previewEntry.HasPasswordBlob;
        HasParseError = previewEntry.HasParseError;
        ParseErrorMessage = previewEntry.ParseErrorMessage;
        HasNameConflict = previewEntry.HasNameConflict;
        ConflictingExistingName = previewEntry.ConflictingExistingName;
        Gateway = previewEntry.Candidate.RdpGateway;
        UnknownKeyCount = previewEntry.UnknownKeyCount;
        HasSkippedMappings = previewEntry.SkippedMappings.Count > 0;
        IsSelected = !previewEntry.HasParseError;
        ConflictResolution = previewEntry.HasNameConflict
            ? RdpConflictResolution.AutoRename
            : RdpConflictResolution.Skip;
    }

    public RdpImportPreviewEntry PreviewEntry { get; }

    public string SourceFileName { get; }

    public string SourceFilePath { get; }

    public string ProposedName { get; }

    public string TargetHost { get; }

    public bool HasPasswordBlob { get; }

    public bool HasParseError { get; }

    public string? ParseErrorMessage { get; }

    public bool HasNameConflict { get; }

    public string? ConflictingExistingName { get; }

    /// <summary>
    /// Gateway the import would commit. It routes the session and its credential exchange, so the
    /// row states it instead of letting it reach the profile unseen.
    /// </summary>
    public string? Gateway { get; }

    public bool HasGateway => !string.IsNullOrWhiteSpace(Gateway);

    public int UnknownKeyCount { get; }

    public bool HasUnknownKeys => UnknownKeyCount > 0;

    public bool HasSkippedMappings { get; }

    public string PasswordText => _localizer["DialogImportRdpStatusPasswordIgnored"];

    public string ParseErrorText => ParseErrorMessage ?? _localizer["DialogImportRdpStatusParseError"];

    public string ConflictText => _localizer.Format("DialogImportRdpStatusConflict", ConflictingExistingName ?? ProposedName);

    public string UnknownKeysText => _localizer.FormatCount(
        UnknownKeyCount,
        "DialogImportRdpStatusUnknownKeysOne",
        "DialogImportRdpStatusUnknownKeys",
        UnknownKeyCount);

    public string SkippedMappingsText => _localizer["DialogImportRdpStatusPartialMapping"];

    public string GatewayText => _localizer.Format("DialogImportRdpStatusGateway", Gateway ?? string.Empty);

    public string ConflictAccessibleName => _localizer.Format("A11yRdpImportConflictForName", ProposedName);

    public string RowAccessibleSummary
    {
        get
        {
            var segments = new List<string> { SourceFileName };

            if (HasParseError)
            {
                segments.Add(_localizer.Format("A11yRdpImportSummaryParseError", ParseErrorText));
            }

            if (HasPasswordBlob)
            {
                segments.Add(PasswordText);
            }

            if (HasNameConflict)
            {
                segments.Add(ConflictText);
            }

            if (HasGateway)
            {
                segments.Add(GatewayText);
            }

            if (HasUnknownKeys)
            {
                segments.Add(UnknownKeysText);
            }

            if (HasSkippedMappings)
            {
                segments.Add(SkippedMappingsText);
            }

            return string.Join(", ", segments);
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private RdpConflictResolution _conflictResolution;
}

public sealed record RdpConflictResolutionOption(
    RdpConflictResolution Value,
    string Label);

public sealed record RdpImportDialogTextOptions
{
    public static RdpImportDialogTextOptions RdpImport { get; } = new();

    public static RdpImportDialogTextOptions ProfileImport { get; } = new()
    {
        TitleKey = "DialogImportProfileTitle",
        SubtitleKey = "DialogImportProfileSubtitle",
        SubtitleOneKey = "DialogImportProfileSubtitleOne",
        ConfirmKey = "DialogImportProfileBtnImportSelected",
        SummaryKey = "DialogImportProfileSummary",
        SelectedKey = "DialogImportProfileCountSelected",
        SelectedOneKey = "DialogImportProfileCountSelectedOne",
        IncludePasswordWarningsInSummary = false
    };

    public string TitleKey { get; init; } = "DialogImportRdpTitle";

    public string SubtitleKey { get; init; } = "DialogImportRdpSubtitle";

    /// <summary>The subtitle when the item count takes the singular.</summary>
    public string SubtitleOneKey { get; init; } = "DialogImportRdpSubtitleOne";

    public string ConfirmKey { get; init; } = "DialogImportRdpBtnImportSelected";

    /// <summary>The summary sentence; its placeholders receive counted fragments.</summary>
    public string SummaryKey { get; init; } = "DialogImportRdpSummary";

    /// <summary>The selected-out-of-total fragment of the summary, counted by the selected items.</summary>
    public string SelectedKey { get; init; } = "DialogImportRdpCountSelected";

    /// <summary>The selected-out-of-total fragment when the selected count takes the singular.</summary>
    public string SelectedOneKey { get; init; } = "DialogImportRdpCountSelectedOne";

    public bool IncludePasswordWarningsInSummary { get; init; } = true;
}
