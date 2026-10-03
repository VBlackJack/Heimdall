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

using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    /// <summary>
    /// The window's title follows an interface language switch.
    /// </summary>
    /// <remarks>
    /// It is worded from the localizer and was set only when the sessions were loaded or
    /// reloaded, so the title bar and the taskbar kept the old language after a switch.
    /// </remarks>
    [Fact]
    public async Task WindowTitle_FollowsALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        await main.LoadCommand.ExecuteAsync(null);
        string englishTitle = main.WindowTitle;
        Assert.False(string.IsNullOrEmpty(englishTitle));

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(localizer.Format("WindowTitle", main.ServerCount), main.WindowTitle);
        Assert.NotEqual(englishTitle, main.WindowTitle);
    }
}
