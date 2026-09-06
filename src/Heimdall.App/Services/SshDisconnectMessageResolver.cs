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
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Resolves the user-visible detail of a session disconnect in the current locale.
/// A detail named by locale key is formatted with its arguments; a classified failure
/// is localized from its failure code the way the connect path does; anything else is
/// shown as relayed.
/// </summary>
internal static class SshDisconnectMessageResolver
{
    /// <summary>
    /// Returns the localized disconnect detail, or null when there is nothing to show.
    /// </summary>
    /// <param name="disconnect">The disconnect to describe.</param>
    /// <param name="localizer">The active localizer; null falls back to the raw key or text.</param>
    /// <param name="targetLabel">Label of the remote end, used by network failure templates.</param>
    public static string? Resolve(
        SshSessionDisconnectInfo disconnect,
        LocalizationManager? localizer,
        string targetLabel)
    {
        ArgumentNullException.ThrowIfNull(disconnect);

        if (!string.IsNullOrWhiteSpace(disconnect.MessageKey))
        {
            if (localizer is null)
            {
                return disconnect.MessageKey;
            }

            object[] arguments = disconnect.MessageArguments
                .Select(static argument => argument ?? string.Empty)
                .ToArray();
            return localizer.Format(disconnect.MessageKey, arguments);
        }

        if (disconnect.Failure is not null && localizer is not null)
        {
            return SshFailureLocalizer.Localize(disconnect.Failure, localizer, targetLabel).Message;
        }

        return disconnect.Message;
    }
}
