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
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The strings the strip stores (tab names, close-button names, group labels, the open-sessions
/// notice) are reworded by <see cref="ConnectionViewModel.RefreshLocalizedText"/> after a language
/// change, instead of staying in the old language until something else touches them.
/// </summary>
public sealed class ConnectionViewModelLocaleRefreshTests
{
    private const string ToolTypeId = "TOOL:PING";
    private const string UserTitle = "srv01";
    private const string EnglishLocale = "en";
    private const string FrenchLocale = "fr";
    private const string CloseKey = "A11yCloseSessionTabNamed";
    private const string ToolsLabelKey = "SessionTabGroupTools";
    private const string SessionLoadNoticeKey = "SessionLoadNoticeText";
    private const int NoticeThreshold = 1;
    private const int NoticeOpenCount = 2;

    [Fact]
    public async Task SessionTabNames_FollowTheLanguage_AndKeepTheUserTitle()
    {
        LocalizationManager localizer = await LoadAsync(EnglishLocale);
        ConnectionViewModel sut = CreateViewModel(localizer);
        SessionTabViewModel tab = sut.AddSession(UserTitle, UserTitle, "SSH");
        tab.PrimaryPane.Status = "Host key mismatch for srv01";
        string englishClose = tab.CloseAccessibleName;
        string englishName = tab.AccessibleName;

        await localizer.SwitchLocaleAsync(FrenchLocale);
        sut.RefreshLocalizedText();

        Assert.NotEqual(englishClose, tab.CloseAccessibleName);
        Assert.Equal(localizer.Format(CloseKey, UserTitle), tab.CloseAccessibleName);
        Assert.NotEqual(englishName, tab.AccessibleName);
        Assert.StartsWith(UserTitle, tab.AccessibleName, StringComparison.Ordinal);
        Assert.Equal(UserTitle, tab.Title);
    }

    [Fact]
    public async Task ToolsGroupLabelAndChipName_AreReworded()
    {
        LocalizationManager localizer = await LoadAsync(EnglishLocale);
        ConnectionViewModel sut = CreateViewModel(localizer);
        SessionTabViewModel tool = sut.AddSession(ToolTypeId, ToolTypeId, ToolTypeId);
        string englishLabel = tool.TabGroupLabel;
        string englishChip = tool.TabGroupAccessibleName;

        await localizer.SwitchLocaleAsync(FrenchLocale);
        sut.RefreshLocalizedText();

        Assert.NotEqual(englishLabel, tool.TabGroupLabel);
        Assert.Equal(localizer[ToolsLabelKey], tool.TabGroupLabel);
        Assert.NotEqual(englishChip, tool.TabGroupAccessibleName);
    }

    [Fact]
    public async Task ShownSessionLoadNotice_IsReworded()
    {
        LocalizationManager localizer = await LoadAsync(EnglishLocale);
        ConnectionViewModel sut = CreateViewModel(localizer);
        sut.AddSession("one", "One", "SSH").HostControl = new object();
        sut.AddSession("two", "Two", "SSH", NoticeThreshold);
        Assert.True(sut.IsSessionLoadNoticeShown);
        string english = sut.SessionLoadNoticeText;

        await localizer.SwitchLocaleAsync(FrenchLocale);
        sut.RefreshLocalizedText();

        Assert.NotEqual(english, sut.SessionLoadNoticeText);
        Assert.Equal(
            localizer.Format(SessionLoadNoticeKey, NoticeOpenCount, NoticeThreshold),
            sut.SessionLoadNoticeText);
        Assert.True(sut.IsSessionLoadNoticeShown);
    }

    [Fact]
    public async Task NoNotice_StaysEmpty()
    {
        LocalizationManager localizer = await LoadAsync(EnglishLocale);
        ConnectionViewModel sut = CreateViewModel(localizer);

        await localizer.SwitchLocaleAsync(FrenchLocale);
        sut.RefreshLocalizedText();

        Assert.Equal(string.Empty, sut.SessionLoadNoticeText);
    }

    private static async Task<LocalizationManager> LoadAsync(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(ViewSource.RepoRoot(), "locales"), locale);
        return localizer;
    }

    private static ConnectionViewModel CreateViewModel(LocalizationManager localizer)
        => new(
            localizer,
            new ConnectionViewModelCloseTests.TrackingDialogService(true),
            new ConnectionViewModelCloseTests.TrackingSplitService(),
            new PaneCloseArbiter(),
            new SessionWindowService(static (_, _) => { }))
        {
            GroupTabsByFolder = true,
        };
}
