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

using System.IO;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// The conflict dialog says how the two files compare, and can replace only what is newer, without
/// changing the choice every row starts with.
/// </summary>
public sealed class FileConflictComparisonTests
{
    private static readonly DateTime Noon = new(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<LocalizationManager> LoadLocalizerAsync()
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return manager;
    }

    private static FileConflictAnalysisItem Conflict(int index, string target = "/srv/a.txt")
        => new(
            index,
            $"source-{index}",
            target,
            HasConflict: true,
            FileConflictItemKind.File,
            FileConflictItemKind.File,
            FileConflictResolutionActions.All);

    private static FileConflictComparison Compare(TimeSpan incomingAfterExisting)
        => new(
            new FileConflictSideInfo(2048, Noon + incomingAfterExisting),
            new FileConflictSideInfo(1024, Noon));

    [Fact]
    public async Task ARowWithDatesSaysWhichFileIsNewer()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        FileConflictDialogViewModel dialog = new(
            [Conflict(0), Conflict(1), Conflict(2)],
            localizer,
            item => item.Index switch
            {
                0 => Compare(TimeSpan.FromHours(1)),
                1 => Compare(TimeSpan.FromHours(-1)),
                _ => Compare(TimeSpan.Zero),
            });

        Assert.True(dialog.HasComparison);
        Assert.True(dialog.Rows[0].IsIncomingNewer);
        Assert.Equal(localizer["DialogFileConflictIncomingNewer"], dialog.Rows[0].ComparisonNote);
        Assert.Equal(localizer["DialogFileConflictIncomingOlder"], dialog.Rows[1].ComparisonNote);
        Assert.Equal(localizer["DialogFileConflictSameTime"], dialog.Rows[2].ComparisonNote);
        Assert.Contains(Heimdall.Core.Utilities.FileSize.Format(2048), dialog.Rows[0].IncomingSummary, StringComparison.Ordinal);
        Assert.Contains(Heimdall.Core.Utilities.FileSize.Format(1024), dialog.Rows[0].ExistingSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferenceWithinTheRoundingOfAServerIsNotNewer()
    {
        FileConflictDialogViewModel dialog = new(
            [Conflict(0)],
            localizer: null,
            _ => Compare(FileConflictDialogViewModel.NewerTolerance));

        Assert.False(dialog.Rows[0].IsIncomingNewer);

        FileConflictDialogViewModel clearlyNewer = new(
            [Conflict(0)],
            localizer: null,
            _ => Compare(FileConflictDialogViewModel.NewerTolerance + TimeSpan.FromSeconds(1)));

        Assert.True(clearlyNewer.Rows[0].IsIncomingNewer);
    }

    [Fact]
    public void ReplaceIfNewer_ReplacesWhatIsNewerAndSkipsTheRest()
    {
        FileConflictDialogViewModel dialog = new(
            [Conflict(0), Conflict(1), Conflict(2)],
            localizer: null,
            item => item.Index == 1 ? Compare(TimeSpan.FromDays(-2)) : Compare(TimeSpan.FromDays(2)));

        dialog.ApplyAllReplaceIfNewerCommand.Execute(null);

        Assert.Equal(
            [FileConflictResolutionChoice.Replace, FileConflictResolutionChoice.Skip, FileConflictResolutionChoice.Replace],
            dialog.Rows.Select(row => row.Resolution));
    }

    [Fact]
    public void ReplaceIfNewer_LeavesARowWithNoDatesWhereItWas()
    {
        FileConflictDialogViewModel dialog = new(
            [Conflict(0), Conflict(1)],
            localizer: null,
            item => item.Index == 0 ? Compare(TimeSpan.FromDays(2)) : null);
        FileConflictResolutionChoice before = dialog.Rows[1].Resolution;

        dialog.ApplyAllReplaceIfNewerCommand.Execute(null);

        Assert.Equal(FileConflictResolutionChoice.Replace, dialog.Rows[0].Resolution);
        Assert.Equal(before, dialog.Rows[1].Resolution);
    }

    [Fact]
    public void WithoutDates_TheDialogOffersNoComparisonAndTheSafeDefaultStays()
    {
        FileConflictDialogViewModel dialog = new([Conflict(0)], localizer: null);

        Assert.False(dialog.HasComparison);
        Assert.False(dialog.Rows[0].HasComparison);
        Assert.Equal(FileConflictResolutionChoice.AutoRename, dialog.Rows[0].Resolution);
    }

    [Fact]
    public void TheDefaultChoiceIsStillTheNonDestructiveOneWhenDatesAreKnown()
    {
        FileConflictDialogViewModel dialog = new([Conflict(0)], null, _ => Compare(TimeSpan.FromDays(9)));

        Assert.Equal(FileConflictResolutionChoice.AutoRename, dialog.Rows[0].Resolution);
    }

    [Fact]
    public async Task TheDialogExplainsThatCancelStopsTheWholeBatch()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();

        FileConflictDialogViewModel dialog = new([Conflict(0)], localizer);

        Assert.Equal(localizer["DialogFileConflictCancelHint"], dialog.CancelHintText);
        Assert.NotEqual("DialogFileConflictCancelHint", dialog.CancelHintText);
    }
}
