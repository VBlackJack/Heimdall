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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    /// <summary>
    /// An RDP tab opened in a forced mode names that mode in the current language after a switch.
    /// </summary>
    /// <remarks>
    /// The suffix is stored on the tab as worded text when the tab opens, so re-announcing the
    /// title alone would repaint the old words. The shell's language refresh now has the
    /// coordinator re-word it.
    /// </remarks>
    [Fact]
    public async Task ForcedRdpModeSuffix_IsRewordedByALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        LocalizationManager localizer = harness.Main.GetLocalizer();
        SessionTabViewModel tab = new()
        {
            Title = "rdp01",
            RdpModeOverride = RdpModeOverride.ForceEmbedded,
            RdpModeOverrideSuffix = localizer["SessionTitleSuffixForcedEmbedded"],
        };
        harness.Main.Connection.ActiveSessions.Add(tab);
        string titleBefore = tab.DisplayTitle;

        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(localizer["SessionTitleSuffixForcedEmbedded"], tab.RdpModeOverrideSuffix);
        Assert.NotEqual(titleBefore, tab.DisplayTitle);
        Assert.Contains(nameof(SessionTabViewModel.DisplayTitle), raised);
    }
}
