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
using Heimdall.App.Localization;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The single status line a native OpenSSH launch shows: neither the capability limit nor the
/// count of entries ssh was not given may hide the other.
/// </summary>
public sealed class OpenSshLaunchStatusTextTests
{
    private static async Task<LocalizationManager> EnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static SshCapabilityNotice AgentKeyNotice()
        => SshCapabilityScope.Evaluate(SshResolvedPath.NativeOpenSsh, false, false, agentKeyChosen: true)!;

    [Fact]
    public async Task NothingToSay_YieldsNoStatus()
    {
        Assert.Null(OpenSshLaunchStatusText.Compose(await EnglishAsync(), null, 0));
    }

    [Fact]
    public async Task OnlyTheCapabilityNotice_IsTheNoticeText()
    {
        LocalizationManager localizer = await EnglishAsync();

        string? text = OpenSshLaunchStatusText.Compose(localizer, AgentKeyNotice(), 0);

        Assert.Equal(localizer[SshLocalizationKeys.StatusOpenSshAgentKeyNotHonoured], text);
    }

    [Fact]
    public async Task OnlyDroppedEntries_AreCountedWithTheirSingularOrPluralWording()
    {
        LocalizationManager localizer = await EnglishAsync();

        Assert.Equal(
            localizer.Format(SshLocalizationKeys.StatusOpenSshEntriesNotAppliedOne, 1),
            OpenSshLaunchStatusText.Compose(localizer, null, 1));
        Assert.Equal(
            localizer.Format(SshLocalizationKeys.StatusOpenSshEntriesNotApplied, 3),
            OpenSshLaunchStatusText.Compose(localizer, null, 3));
    }

    [Fact]
    public async Task BothAtOnce_AreInOneTextThatKeepsEachOfThem()
    {
        LocalizationManager localizer = await EnglishAsync();

        string? text = OpenSshLaunchStatusText.Compose(localizer, AgentKeyNotice(), 2);

        Assert.NotNull(text);
        Assert.Contains(localizer[SshLocalizationKeys.StatusOpenSshAgentKeyNotHonoured], text, StringComparison.Ordinal);
        Assert.Contains(localizer.Format(SshLocalizationKeys.StatusOpenSshEntriesNotApplied, 2), text, StringComparison.Ordinal);
    }
}
