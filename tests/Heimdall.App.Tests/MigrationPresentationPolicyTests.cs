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

namespace Heimdall.App.Tests;

public sealed class MigrationPresentationPolicyTests
{
    [Fact]
    public async Task CleanResultUsesInfoPresentation()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        MigrationResult result = new()
        {
            Success = true,
            ServersExamined = 2,
            ServersImported = 2
        };

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localizer);

        Assert.Equal(MigrationPresentationKind.Info, presentation.Kind);
        Assert.Equal(localizer.Format("MigrationSuccess", 2), presentation.Message);
    }

    [Fact]
    public async Task PartialResultUsesWarningWithCountsAndIdentity()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        MigrationResult result = new()
        {
            Success = true,
            ServersExamined = 2,
            ServersImported = 1,
            Error = "RAW EXCEPTION Int32 DO-NOT-DISPLAY-FAKE-SECRET",
            Warnings =
            [
                new MigrationWarning(
                    2,
                    "Rejected profile",
                    MigrationWarningReason.InvalidLegacyField)
            ]
        };

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localizer);

        Assert.Equal(MigrationPresentationKind.Warning, presentation.Kind);
        Assert.Contains(
            localizer.Format("MigrationPartialSummary", 2, 1, 1),
            presentation.Message,
            StringComparison.Ordinal);
        Assert.Contains("Rejected profile", presentation.Message, StringComparison.Ordinal);
        Assert.Contains(
            localizer["MigrationWarningInvalidLegacyField"],
            presentation.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RAW EXCEPTION", presentation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", presentation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "DO-NOT-DISPLAY-FAKE-SECRET",
            presentation.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialResultUsesLocalizedFallbackForMissingIdentity()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        MigrationResult result = new()
        {
            Success = true,
            ServersExamined = 1,
            ServersImported = 0,
            Warnings =
            [
                new MigrationWarning(
                    1,
                    null,
                    MigrationWarningReason.UnexpectedMappingFailure)
            ]
        };

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localizer);

        Assert.Equal(MigrationPresentationKind.Warning, presentation.Kind);
        Assert.Contains(
            localizer["MigrationWarningUnnamedProfile"],
            presentation.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            localizer["MigrationWarningUnexpectedMappingFailure"],
            presentation.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FatalResultUsesExistingWarningPresentation()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        MigrationResult result = new()
        {
            Success = false,
            Error = "fatal detail"
        };

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localizer);

        Assert.Equal(MigrationPresentationKind.Warning, presentation.Kind);
        Assert.Equal(
            localizer.Format("MigrationFailed", "fatal detail"),
            presentation.Message);
    }

    [Fact]
    public async Task PartialResultCapsWarningsAndReportsOmittedCount()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        int warningCount = MigrationPresentationPolicy.MaxDisplayedWarnings + 2;
        MigrationResult result = new()
        {
            Success = true,
            ServersExamined = warningCount,
            ServersImported = 0
        };
        for (int index = 1; index <= warningCount; index++)
        {
            result.Warnings.Add(new MigrationWarning(
                index,
                $"Rejected-{index}",
                MigrationWarningReason.InvalidLegacyField));
        }

        MigrationPresentation presentation = MigrationPresentationPolicy.Create(
            result,
            localizer);

        Assert.Equal(MigrationPresentationKind.Warning, presentation.Kind);
        for (int index = 1; index <= MigrationPresentationPolicy.MaxDisplayedWarnings; index++)
        {
            Assert.Contains($"Rejected-{index}", presentation.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            $"Rejected-{MigrationPresentationPolicy.MaxDisplayedWarnings + 1}",
            presentation.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            localizer.Format("MigrationWarningsOmitted", 2),
            presentation.Message,
            StringComparison.Ordinal);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
