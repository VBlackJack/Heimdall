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

using Heimdall.App.Services.Handlers;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Services;

/// <summary>
/// Lists, one row each, everything a profile would run that the execution gate asks about, in
/// the order and under the conditions <see cref="ProfileExecutionTrust"/> counts them.
/// </summary>
/// <remarks>
/// The profile may come from a file, so every value is written out whole and visibly: nothing is
/// truncated, and a control, bidi, zero-width or line-separator character cannot start a new row
/// or reorder what is read.
/// </remarks>
internal static class ExecutionTrustDescriber
{
    private const string ProgramKey = "ServerDialogExecutionTrustProgram";
    private const string IcaFileKey = "ServerDialogExecutionTrustIcaFile";
    private const string CommandKey = "ServerDialogExecutionTrustCommand";
    private const string LibraryCommandKey = "ServerDialogExecutionTrustLibraryCommand";

    /// <summary>Joins the parameters of a linked Command Library entry.</summary>
    private const string ParameterSeparator = ", ";

    /// <summary>The row of a linked entry that gets no parameter from the profile.</summary>
    private const string LibraryCommandNoParametersKey = "ServerDialogExecutionTrustLibraryCommandNoParameters";

    /// <summary>
    /// Describes what <paramref name="profile"/> would run.
    /// </summary>
    /// <param name="profile">The profile as it would be saved.</param>
    /// <param name="linkedTitles">The titles of linked Command Library entries, by entry id.</param>
    /// <param name="localizer">The localizer the rows are written with.</param>
    public static string Describe(
        ServerProfileDto profile,
        IReadOnlyDictionary<string, string> linkedTitles,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(linkedTitles);
        ArgumentNullException.ThrowIfNull(localizer);

        List<string> rows = [];

        if (ProfileExecutionTrust.CarriesLocalExecutionPayload(profile))
        {
            rows.Add(DescribeExecutionRow(profile, localizer));
        }

        foreach (PostConnectStep? step in profile.PostConnectSteps)
        {
            if (step is null || !step.Enabled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(step.CommandLibraryId))
            {
                rows.Add(DescribeLibraryStep(step, step.CommandLibraryId, linkedTitles, localizer));
            }
            else if (!string.IsNullOrWhiteSpace(step.Input))
            {
                rows.Add(localizer.Format(CommandKey, Visible(step.Input)));
            }
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static string DescribeExecutionRow(ServerProfileDto profile, LocalizationManager localizer)
    {
        if (string.Equals(profile.ConnectionType, "WEB", StringComparison.OrdinalIgnoreCase))
        {
            // A command too long to be shown whole is refused rather than shown cut: the end of
            // the arguments could hold a flag nobody read.
            return localizer.Format(
                ProgramKey,
                WebExternalLaunch.FitsDescription(profile.WebExternalProgram, profile.WebExternalArguments)
                    ? WebExternalLaunch.DescribeCommand(profile.WebExternalProgram, profile.WebExternalArguments)
                    : localizer[WebExternalLaunch.CommandTooLongErrorKey]);
        }

        if (string.Equals(profile.ConnectionType, "CITRIX", StringComparison.OrdinalIgnoreCase))
        {
            return localizer.Format(IcaFileKey, UntrustedText.MakeVisible(profile.CitrixIcaFilePath, UntrustedText.NoLengthLimit));
        }

        return localizer.Format(ProgramKey, ServerListViewModel.DescribeLocalShellExecution(profile, localizer));
    }

    private static string DescribeLibraryStep(
        PostConnectStep step,
        string libraryId,
        IReadOnlyDictionary<string, string> linkedTitles,
        LocalizationManager localizer)
    {
        string title = linkedTitles.TryGetValue(libraryId, out string? known) && !string.IsNullOrWhiteSpace(known)
            ? known
            : libraryId;

        if (step.CommandLibraryParams is not { Count: > 0 })
        {
            return localizer.Format(LibraryCommandNoParametersKey, Visible(title));
        }

        string parameters = string.Join(
            ParameterSeparator,
            step.CommandLibraryParams.Select(pair => $"{Visible(pair.Key)} = {Quoted(pair.Value)}"));

        return localizer.Format(LibraryCommandKey, Visible(title), parameters);
    }

    /// <summary>
    /// A value made visible and safe to show between double quotes: a backslash is doubled first,
    /// then a quote is escaped, so a value ending in a backslash cannot turn the escaped quote
    /// after it into a closing one.
    /// </summary>
    private static string Visible(string? value)
    {
        // The backslash is doubled on the raw value, before the visible escapes (which contain
        // backslashes of their own) are written, so those are not doubled in turn.
        string raw = (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal);
        return UntrustedText.MakeVisible(raw, UntrustedText.NoLengthLimit)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    /// <summary>A value made visible and wrapped in double quotes.</summary>
    private static string Quoted(string? value) => "\"" + Visible(value) + "\"";
}
