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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>The decisions accepted by the file-conflict batch dialog.</summary>
/// <param name="Decisions">One decision for every displayed collision.</param>
public sealed record FileConflictDialogResult(IReadOnlyList<FileConflictDecision> Decisions);

/// <summary>Abstraction used by transfer ViewModels to raise the WPF batch dialog.</summary>
internal interface IFileConflictDialogPresenter
{
    Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel);
}

/// <summary>Labelled resolution option displayed by each conflict row.</summary>
/// <param name="Value">Resolution value returned to the planner.</param>
/// <param name="Label">Localized label.</param>
public sealed record FileConflictResolutionOption(
    FileConflictResolutionChoice Value,
    string Label);

/// <summary>ViewModel for resolving every collision in one pre-transfer batch.</summary>
public sealed partial class FileConflictDialogViewModel : ObservableObject
{
    public FileConflictDialogViewModel(
        IReadOnlyList<FileConflictAnalysisItem> conflicts,
        LocalizationManager? localizer)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Any(item => !item.HasConflict))
        {
            throw new ArgumentException("Only conflicting items may be displayed.", nameof(conflicts));
        }

        string L(string key) => localizer?[key] ?? key;

        DialogTitle = L("DialogFileConflictTitle");
        DialogHint = L("DialogFileConflictHint");
        SummaryText = localizer?.Format("DialogFileConflictSummary", conflicts.Count)
            ?? $"{conflicts.Count} conflict(s)";
        ApplyToAllText = L("DialogFileConflictApplyAll");
        ApplyAllSkipText = L("DialogFileConflictActionSkip");
        ApplyAllReplaceText = L("DialogFileConflictActionReplace");
        ApplyAllAutoRenameText = L("DialogFileConflictActionAutoRename");
        TargetColumnHeader = L("DialogFileConflictColTarget");
        ActionColumnHeader = L("DialogFileConflictColAction");
        ApplyText = L("DialogFileConflictApply");
        CancelText = L("BtnCancel");

        ConflictOptions =
        [
            new FileConflictResolutionOption(
                FileConflictResolutionChoice.Skip,
                L("DialogFileConflictActionSkip")),
            new FileConflictResolutionOption(
                FileConflictResolutionChoice.Replace,
                L("DialogFileConflictActionReplace")),
            new FileConflictResolutionOption(
                FileConflictResolutionChoice.AutoRename,
                L("DialogFileConflictActionAutoRename")),
        ];

        Rows = new ObservableCollection<FileConflictRowViewModel>(
            conflicts.Select(item => new FileConflictRowViewModel(item)));
    }

    public event Action<bool>? CloseRequested;

    public string DialogTitle { get; }

    public string DialogHint { get; }

    public string SummaryText { get; }

    public string ApplyToAllText { get; }

    public string ApplyAllSkipText { get; }

    public string ApplyAllReplaceText { get; }

    public string ApplyAllAutoRenameText { get; }

    public string TargetColumnHeader { get; }

    public string ActionColumnHeader { get; }

    public string ApplyText { get; }

    public string CancelText { get; }

    public IReadOnlyList<FileConflictResolutionOption> ConflictOptions { get; }

    public ObservableCollection<FileConflictRowViewModel> Rows { get; }

    public FileConflictDialogResult? Result { get; private set; }

    [RelayCommand]
    private void ApplyAllSkip() => ApplyResolutionToAll(FileConflictResolutionChoice.Skip);

    [RelayCommand]
    private void ApplyAllReplace() => ApplyResolutionToAll(FileConflictResolutionChoice.Replace);

    [RelayCommand]
    private void ApplyAllAutoRename() => ApplyResolutionToAll(FileConflictResolutionChoice.AutoRename);

    [RelayCommand]
    private void Apply()
    {
        Result = new FileConflictDialogResult(
            Rows.Select(row => new FileConflictDecision(row.ItemIndex, row.Resolution)).ToList());
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(false);
    }

    private void ApplyResolutionToAll(FileConflictResolutionChoice resolution)
    {
        foreach (FileConflictRowViewModel row in Rows)
        {
            row.Resolution = resolution;
        }
    }
}

/// <summary>One colliding destination displayed in the batch dialog.</summary>
public sealed partial class FileConflictRowViewModel : ObservableObject
{
    internal FileConflictRowViewModel(FileConflictAnalysisItem item)
    {
        ItemIndex = item.Index;
        SourceIdentity = item.SourceIdentity;
        TargetPath = item.TargetPath;
        Resolution = FileConflictResolutionChoice.AutoRename;
    }

    public int ItemIndex { get; }

    public string SourceIdentity { get; }

    public string TargetPath { get; }

    [ObservableProperty]
    private FileConflictResolutionChoice _resolution;
}
