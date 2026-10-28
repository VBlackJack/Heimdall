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
using System.IO;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Matching;

namespace Heimdall.App.Tests;

/// <summary>
/// The rows that were announced by their type name now carry a name of their own.
/// </summary>
/// <remarks>
/// The container of each row reads <c>IAccessibleItemViewModel.AccessibleName</c> (the markup side is
/// held by <c>ItemContainerNamingCensusTests</c>); these tests hold what the name says and that it
/// follows the row as it changes.
/// </remarks>
public sealed class AccessibleItemNameTests
{
    [Fact]
    public void ARegexMatch_IsNamedByWhatItShowsOnScreen()
    {
        MatchDisplayItem match = new(1, new RegexMatchInfo(7, 3, "abc", []));
        match.ApplyLocalization(localizer: null);

        Assert.IsAssignableFrom<IAccessibleItemViewModel>(match);
        Assert.False(string.IsNullOrWhiteSpace(match.DisplayText));
        Assert.Equal(match.DisplayText, match.AccessibleName);
    }

    [Fact]
    public void ARegexMatchNameFollowsItsText()
    {
        MatchDisplayItem match = new(1, new RegexMatchInfo(0, 2, "xy", []));
        List<string?> changed = [];
        ((INotifyPropertyChanged)match).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        match.ApplyLocalization(localizer: null);

        Assert.Contains(nameof(IAccessibleItemViewModel.AccessibleName), changed);
    }

    [Fact]
    public void ARegexMatchBeforeItIsLocalized_IsNamedByItsValue_NotLeftWithoutAName()
    {
        MatchDisplayItem match = new(1, new RegexMatchInfo(0, 2, "xy", []));

        Assert.Equal("xy", match.AccessibleName);
    }

    [Fact]
    public void ANoteRow_ListsWhatItShowsBesidesItsTitle_WithoutThePipeAndTheHashMarks()
    {
        NoteListItem note = new(
            FilePath: @"C:\notes\ops\server-setup.md",
            RelativePath: "ops/server-setup.md",
            Title: "Server Setup",
            Summary: "Checklist",
            Tags: ["infra", "prod"],
            LastModifiedUtc: new DateTime(2026, 3, 26, 15, 0, 0, DateTimeKind.Utc),
            LastModifiedDisplay: "2026-03-26 16:00");

        NoteTreeNode folder = Assert.Single(NoteTreeNode.BuildTree([note], @"C:\notes"));
        NoteTreeNode row = Assert.Single(folder.Children);

        Assert.Equal("2026-03-26 16:00, server-setup.md, infra, prod", row.DisplayDetail);
        Assert.Equal(string.Empty, folder.DisplayDetail);
    }

    [Fact]
    public void ANoteWithoutTags_HasNoTrailingSeparator()
    {
        NoteListItem note = new(
            FilePath: @"C:\notes\todo.md",
            RelativePath: "todo.md",
            Title: "Todo",
            Summary: string.Empty,
            Tags: [],
            LastModifiedUtc: new DateTime(2026, 3, 26, 15, 0, 0, DateTimeKind.Utc),
            LastModifiedDisplay: "2026-03-26 16:00");

        NoteTreeNode row = Assert.Single(NoteTreeNode.BuildTree([note], @"C:\notes"));

        Assert.Equal("2026-03-26 16:00, todo.md", row.DisplayDetail);
    }
}

/// <summary>
/// The name of a post-connect step row, read through the shared localization source: the tests
/// that initialize it run apart from the others.
/// </summary>
[Collection("Session failure stage localization")]
public sealed class PostConnectStepAccessibleNameTests
{
    [Fact]
    public async Task APostConnectStep_IsNamedByItsCommand()
    {
        await UseEnglishAsync();
        PostConnectStepItemViewModel step = new() { Input = "systemctl status nginx" };

        Assert.IsAssignableFrom<IAccessibleItemViewModel>(step);
        Assert.Equal("Step systemctl status nginx", step.AccessibleName);
    }

    [Fact]
    public async Task ALinkedPostConnectStep_IsNamedByTheActionItIsLinkedTo()
    {
        await UseEnglishAsync();
        PostConnectStepItemViewModel step = new()
        {
            Input = "ignored while linked",
            CommandLibraryId = "action-1",
            LinkedActionTitle = "Restart the print spooler"
        };

        Assert.Equal("Step Restart the print spooler", step.AccessibleName);
    }

    [Fact]
    public async Task AnEmptyPostConnectStep_IsStillNamed()
    {
        await UseEnglishAsync();
        PostConnectStepItemViewModel step = new();

        // An empty name would fall back to the type name, which is the defect.
        Assert.Equal("Step empty", step.AccessibleName);
    }

    [Fact]
    public async Task APostConnectStepName_FollowsWhatIsTypedAndWhatItIsLinkedTo()
    {
        await UseEnglishAsync();
        PostConnectStepItemViewModel step = new();
        List<string?> changed = [];
        ((INotifyPropertyChanged)step).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        step.Input = "uptime";
        Assert.Contains(nameof(IAccessibleItemViewModel.AccessibleName), changed);

        changed.Clear();
        step.CommandLibraryId = "action-1";
        Assert.Contains(nameof(IAccessibleItemViewModel.AccessibleName), changed);

        changed.Clear();
        step.LinkedActionTitle = "Show uptime";
        Assert.Contains(nameof(IAccessibleItemViewModel.AccessibleName), changed);
    }

    private static async Task UseEnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        LocalizationSource.Instance.Initialize(localizer);
    }
}
