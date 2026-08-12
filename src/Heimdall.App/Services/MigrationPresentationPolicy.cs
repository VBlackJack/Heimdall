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

using Heimdall.Core.Localization;

namespace Heimdall.App.Services;

internal enum MigrationPresentationKind
{
    Info,
    Warning
}

internal sealed record MigrationPresentation(
    MigrationPresentationKind Kind,
    string Message);

internal static class MigrationPresentationPolicy
{
    internal const int MaxDisplayedWarnings = 5;

    internal static MigrationPresentation Create(
        MigrationResult result,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        if (!result.Success)
        {
            return new MigrationPresentation(
                MigrationPresentationKind.Warning,
                localizer.Format("MigrationFailed", result.Error ?? string.Empty));
        }

        if (result.ServersSkipped == 0)
        {
            return new MigrationPresentation(
                MigrationPresentationKind.Info,
                localizer.Format("MigrationSuccess", result.ServersImported));
        }

        List<string> lines =
        [
            localizer.Format(
                "MigrationPartialSummary",
                result.ServersExamined,
                result.ServersImported,
                result.ServersSkipped)
        ];

        foreach (MigrationWarning warning in result.Warnings.Take(MaxDisplayedWarnings))
        {
            string identity = warning.Identity
                ?? localizer["MigrationWarningUnnamedProfile"];
            string reason = GetLocalizedReason(warning.Reason, localizer);
            lines.Add(localizer.Format(
                "MigrationPartialItem",
                warning.Index,
                identity,
                reason));
        }

        int omittedCount = result.ServersSkipped - Math.Min(
            result.ServersSkipped,
            MaxDisplayedWarnings);
        if (omittedCount > 0)
        {
            lines.Add(localizer.Format("MigrationWarningsOmitted", omittedCount));
        }

        return new MigrationPresentation(
            MigrationPresentationKind.Warning,
            string.Join(Environment.NewLine, lines));
    }

    private static string GetLocalizedReason(
        MigrationWarningReason reason,
        LocalizationManager localizer)
    {
        string key = reason switch
        {
            MigrationWarningReason.InvalidLegacyField => "MigrationWarningInvalidLegacyField",
            _ => "MigrationWarningUnexpectedMappingFailure"
        };
        return localizer[key];
    }
}
