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

using Heimdall.App.Localization;
using Heimdall.Core.Localization;
using Heimdall.Ssh;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services;

/// <summary>
/// Turns the locale keys carried by SSH-layer results into sentences in the user's
/// language. The SSH layer names a sentence it composed itself (a port already taken,
/// a cancelled establishment, a missing key file) and supplies its arguments; this is
/// the one place that formats them, so no English composed below the application
/// reaches the status bar or the connection state.
/// </summary>
internal static class TunnelFailureMessageResolver
{
    /// <summary>
    /// Returns the result with <see cref="TunnelResult.ErrorMessage"/> replaced by the
    /// localized sentence when the result carries a locale key; unchanged otherwise.
    /// </summary>
    public static TunnelResult Localize(TunnelResult result, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        if (result.Success || string.IsNullOrWhiteSpace(result.MessageKey))
        {
            return result;
        }

        return result with { ErrorMessage = Format(localizer, result.MessageKey, result.MessageArguments) };
    }

    /// <summary>
    /// Returns the plink result with <see cref="PlinkTunnelResult.ErrorMessage"/> replaced
    /// by the localized sentence when the result carries a locale key; unchanged otherwise.
    /// </summary>
    public static PlinkTunnelResult Localize(PlinkTunnelResult result, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        if (result.Success || string.IsNullOrWhiteSpace(result.MessageKey))
        {
            return result;
        }

        return result with { ErrorMessage = Format(localizer, result.MessageKey, result.MessageArguments) };
    }

    /// <summary>
    /// Resolves the sentence of a failed preflight: a message the catalogue knows is a
    /// key and is formatted with the result's arguments, an unknown message is shown as
    /// relayed, and an empty one falls back to the generic preflight sentence.
    /// </summary>
    public static string ResolvePreflightMessage(PreflightResult? preflight, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        string? messageOrKey = preflight?.Message;
        if (string.IsNullOrWhiteSpace(messageOrKey))
        {
            return localizer[SshLocalizationKeys.ErrorPreflightFailed];
        }

        return localizer.HasKey(messageOrKey)
            ? Format(localizer, messageOrKey, preflight!.MessageArguments)
            : messageOrKey;
    }

    private static string Format(LocalizationManager localizer, string key, IReadOnlyList<object?> arguments)
    {
        object[] formatArguments = arguments
            .Select(static argument => argument ?? string.Empty)
            .ToArray();
        return localizer.Format(key, formatArguments);
    }
}
