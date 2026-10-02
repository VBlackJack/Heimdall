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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;
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

/// <summary>What is known of one side of a collision.</summary>
/// <param name="Size">The size in bytes.</param>
/// <param name="ModifiedUtc">The modification time, in UTC.</param>
public readonly record struct FileConflictSideInfo(long Size, DateTime ModifiedUtc);

/// <summary>The incoming file set against the file it would replace.</summary>
/// <param name="Incoming">The file being transferred.</param>
/// <param name="Existing">The file already at the destination.</param>
public sealed record FileConflictComparison(FileConflictSideInfo Incoming, FileConflictSideInfo Existing);

/// <summary>ViewModel for resolving every collision in one pre-transfer batch.</summary>
public sealed partial class FileConflictDialogViewModel : ObservableObject
{
    /// <summary>
    /// How much newer the incoming file must be to count as newer. Remote servers round their
    /// modification times (FTP listings to the minute, some file systems to two seconds), so a
    /// smaller difference is rounding, not a newer file.
    /// </summary>
    internal static readonly TimeSpan NewerTolerance = TimeSpan.FromSeconds(2);

    /// <param name="conflicts">The colliding items, each of which needs a decision.</param>
    /// <param name="localizer">The localizer for the dialog text.</param>
    /// <param name="compare">
    /// Optional: the size and date of both sides of an item, when the caller knows them. With it the
    /// rows say which file is newer, and the dialog offers to replace only what is newer.
    /// </param>
    public FileConflictDialogViewModel(
        IReadOnlyList<FileConflictAnalysisItem> conflicts,
        LocalizationManager? localizer,
        Func<FileConflictAnalysisItem, FileConflictComparison?>? compare = null)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Any(item => !item.HasConflict))
        {
            throw new ArgumentException("Only conflicting items may be displayed.", nameof(conflicts));
        }

        string L(string key) => localizer?[key] ?? key;

        DialogTitle = L("DialogFileConflictTitle");
        DialogHint = L("DialogFileConflictHint");
        SummaryText = localizer?.FormatCount(
                conflicts.Count,
                "DialogFileConflictSummaryOne",
                "DialogFileConflictSummary",
                conflicts.Count)
            ?? "DialogFileConflictSummary";
        ApplyToAllText = L("DialogFileConflictApplyAll");
        ApplyAllSkipText = L("DialogFileConflictActionSkip");
        ApplyAllReplaceText = L("DialogFileConflictActionReplace");
        ApplyAllAutoRenameText = L("DialogFileConflictActionAutoRename");
        ApplyAllReplaceIfNewerText = L("DialogFileConflictActionReplaceIfNewer");
        CancelHintText = L("DialogFileConflictCancelHint");
        TargetColumnHeader = L("DialogFileConflictColTarget");
        ActionColumnHeader = L("DialogFileConflictColAction");
        ApplyText = L("DialogFileConflictApply");
        CancelText = L("BtnCancel");
        DirectorySkipDetailText = L("DialogFileConflictDirectorySkipDetail");

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
            conflicts.Select(item => new FileConflictRowViewModel(
                item,
                ConflictOptions,
                DirectorySkipDetailText,
                compare?.Invoke(item),
                localizer)));
        HasComparison = Rows.Any(row => row.HasComparison);
    }

    public event Action<bool>? CloseRequested;

    public string DialogTitle { get; }

    public string DialogHint { get; }

    public string SummaryText { get; }

    public string ApplyToAllText { get; }

    public string ApplyAllSkipText { get; }

    public string ApplyAllReplaceText { get; }

    public string ApplyAllAutoRenameText { get; }

    public string ApplyAllReplaceIfNewerText { get; }

    /// <summary>The line that says what Cancel does: it stops the whole batch, not one row.</summary>
    public string CancelHintText { get; }

    /// <summary>Whether the rows carry sizes and dates, so replacing only what is newer makes sense.</summary>
    public bool HasComparison { get; }

    public string TargetColumnHeader { get; }

    public string ActionColumnHeader { get; }

    public string ApplyText { get; }

    public string CancelText { get; }

    public string DirectorySkipDetailText { get; }

    public IReadOnlyList<FileConflictResolutionOption> ConflictOptions { get; }

    public ObservableCollection<FileConflictRowViewModel> Rows { get; }

    public FileConflictDialogResult? Result { get; private set; }

    [RelayCommand]
    private void ApplyAllSkip() => ApplyResolutionToAll(FileConflictResolutionChoice.Skip);

    [RelayCommand]
    private void ApplyAllReplace() => ApplyResolutionToAll(FileConflictResolutionChoice.Replace);

    [RelayCommand]
    private void ApplyAllAutoRename() => ApplyResolutionToAll(FileConflictResolutionChoice.AutoRename);

    /// <summary>
    /// Replaces the destinations the incoming file is newer than and skips the rest. A row with no
    /// dates to compare keeps the choice it had.
    /// </summary>
    [RelayCommand]
    private void ApplyAllReplaceIfNewer()
    {
        foreach (FileConflictRowViewModel row in Rows)
        {
            if (!row.HasComparison)
            {
                continue;
            }

            FileConflictResolutionChoice choice = row.IsIncomingNewer
                ? FileConflictResolutionChoice.Replace
                : FileConflictResolutionChoice.Skip;
            if (row.Allows(choice))
            {
                row.Resolution = choice;
            }
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Rows.Any(row => !row.Allows(row.Resolution)))
        {
            throw new InvalidOperationException("A conflict row contains a forbidden resolution.");
        }

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
            if (row.Allows(resolution))
            {
                row.Resolution = resolution;
            }
        }
    }
}

/// <summary>One colliding destination displayed in the batch dialog.</summary>
public sealed partial class FileConflictRowViewModel : ObservableObject
{
    internal FileConflictRowViewModel(
        FileConflictAnalysisItem item,
        IReadOnlyList<FileConflictResolutionOption> allOptions,
        string directorySkipDetailText,
        FileConflictComparison? comparison = null,
        LocalizationManager? localizer = null)
    {
        ItemIndex = item.Index;
        SourceIdentity = item.SourceIdentity;
        TargetPath = item.TargetPath;
        ConflictOptions = allOptions
            .Where(option => Allows(item.AllowedActions, option.Value))
            .ToList();
        if (ConflictOptions.Count == 0)
        {
            throw new ArgumentException(
                $"Conflicting item {item.Index} has no allowed resolution.",
                nameof(item));
        }

        Resolution = ConflictOptions.Any(
            option => option.Value == FileConflictResolutionChoice.AutoRename)
            ? FileConflictResolutionChoice.AutoRename
            : ConflictOptions[0].Value;
        DetailText = item.PlannedKind == FileConflictItemKind.Directory
            && item.AllowedActions == FileConflictResolutionActions.Skip
                ? directorySkipDetailText
                : string.Empty;

        if (comparison is not null)
        {
            HasComparison = true;
            IsIncomingNewer = comparison.Incoming.ModifiedUtc - comparison.Existing.ModifiedUtc
                > FileConflictDialogViewModel.NewerTolerance;
            string Key(string key) => localizer?[key] ?? key;
            string Describe(string key, FileConflictSideInfo side)
                => localizer?.Format(key, FileSize.Format(side.Size), FormatTime(side.ModifiedUtc)) ?? key;

            IncomingSummary = Describe("DialogFileConflictIncomingInfo", comparison.Incoming);
            ExistingSummary = Describe("DialogFileConflictExistingInfo", comparison.Existing);
            ComparisonNote = IsIncomingNewer
                ? Key("DialogFileConflictIncomingNewer")
                : comparison.Existing.ModifiedUtc - comparison.Incoming.ModifiedUtc > FileConflictDialogViewModel.NewerTolerance
                    ? Key("DialogFileConflictIncomingOlder")
                    : Key("DialogFileConflictSameTime");
        }
    }

    /// <summary>Whether the sizes and dates of both sides are known.</summary>
    public bool HasComparison { get; }

    /// <summary>Whether the incoming file is newer than the one it would replace, beyond rounding.</summary>
    public bool IsIncomingNewer { get; }

    /// <summary>The incoming file's size and date, in words.</summary>
    public string IncomingSummary { get; } = string.Empty;

    /// <summary>The existing file's size and date, in words.</summary>
    public string ExistingSummary { get; } = string.Empty;

    /// <summary>Which of the two is newer, in words.</summary>
    public string ComparisonNote { get; } = string.Empty;

    private static string FormatTime(DateTime utc)
        => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public int ItemIndex { get; }

    public string SourceIdentity { get; }

    public string TargetPath { get; }

    public IReadOnlyList<FileConflictResolutionOption> ConflictOptions { get; }

    public string DetailText { get; }

    public bool HasDetail => !string.IsNullOrEmpty(DetailText);

    internal bool Allows(FileConflictResolutionChoice resolution)
        => ConflictOptions.Any(option => option.Value == resolution);

    private static bool Allows(
        FileConflictResolutionActions allowedActions,
        FileConflictResolutionChoice resolution)
    {
        FileConflictResolutionActions action = resolution switch
        {
            FileConflictResolutionChoice.Skip => FileConflictResolutionActions.Skip,
            FileConflictResolutionChoice.Replace => FileConflictResolutionActions.Replace,
            FileConflictResolutionChoice.AutoRename => FileConflictResolutionActions.AutoRename,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };

        return (allowedActions & action) == action;
    }

    [ObservableProperty]
    private FileConflictResolutionChoice _resolution;
}
