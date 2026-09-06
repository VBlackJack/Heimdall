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
using Heimdall.Core.Localization;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// The mapper had no test file: three of its four messages had never been resolved by a
/// test in either language, so a renamed catalogue key would have shown the raw
/// identifier to the user.
/// </summary>
public sealed class UpdateRelaunchOutcomeTextTests
{
    [Theory]
    [InlineData(UpdateRelaunchOutcome.None, null)]
    [InlineData(UpdateRelaunchOutcome.Succeeded, null)]
    [InlineData(UpdateRelaunchOutcome.NotApplied, "UpdateBannerOutcomeNotApplied")]
    [InlineData(UpdateRelaunchOutcome.CancelledByUser, "UpdateBannerOutcomeCancelled")]
    [InlineData(UpdateRelaunchOutcome.InstallerFailed, "UpdateBannerOutcomeInstallerFailed")]
    [InlineData(UpdateRelaunchOutcome.IntegrityRejected, "UpdateBannerOutcomeIntegrityRejected")]
    [InlineData(UpdateRelaunchOutcome.ApplicationStillRunning, "UpdateBannerOutcomeAppStillRunning")]
    public void StatusKey_MapsEveryDeclaredOutcome(UpdateRelaunchOutcome outcome, string? expectedKey)
    {
        Assert.Equal(expectedKey, UpdateRelaunchOutcomeText.StatusKey(outcome));
    }

    [Fact]
    public void StatusKey_CoversTheWholeEnum()
    {
        foreach (UpdateRelaunchOutcome outcome in Enum.GetValues<UpdateRelaunchOutcome>())
        {
            bool silent = outcome is UpdateRelaunchOutcome.None or UpdateRelaunchOutcome.Succeeded;
            Assert.Equal(silent, UpdateRelaunchOutcomeText.StatusKey(outcome) is null);
        }
    }

    [Fact]
    public void StatusKey_UnknownOutcome_InventsNoMessage()
    {
        Assert.Null(UpdateRelaunchOutcomeText.StatusKey((UpdateRelaunchOutcome)999));
    }

    /// <remarks>
    /// HasKey rather than Format: Format returns the key on a miss, so it cannot tell a
    /// hit from a miss.
    /// </remarks>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task EveryKey_ExistsInTheCatalogue(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        foreach (UpdateRelaunchOutcome outcome in Enum.GetValues<UpdateRelaunchOutcome>())
        {
            string? key = UpdateRelaunchOutcomeText.StatusKey(outcome);
            if (key is not null)
            {
                Assert.True(localizer.HasKey(key), $"{locale} has no {key}");
            }
        }
    }
}
