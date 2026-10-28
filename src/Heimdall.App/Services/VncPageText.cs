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

using System.Text.Json;

namespace Heimdall.App.Services;

/// <summary>
/// The words the embedded VNC page shows, localized by the host. The page carries none of its
/// own: it takes these from the connect message and, for what it must show before that message
/// can exist (the loading label, and the failure of its own library), from a script the host adds
/// to the document before any page script runs.
/// </summary>
internal static class VncPageText
{
    internal const string KeyDisconnected = "StatusVncDisconnected";
    internal const string KeyConnectionLost = "ErrorVncConnectionLost";
    internal const string KeyInitFailed = "ErrorVncClientInitFailed";
    internal const string KeyLoading = "StatusVncConnecting";
    internal const string KeyLibraryFailedTitle = "ErrorVncLibraryFailedTitle";
    internal const string KeyLibraryFailedMessage = "ErrorVncLibraryFailedMessage";

    internal const string FieldDisconnected = "disconnected";
    internal const string FieldConnectionLost = "connectionLost";
    internal const string FieldInitFailed = "initFailed";
    internal const string FieldLoading = "loading";
    internal const string FieldLibraryFailedTitle = "libraryFailedTitle";
    internal const string FieldLibraryFailedMessage = "libraryFailedMessage";

    /// <summary>The page global the bootstrap script fills; vnc.html reads the same name.</summary>
    internal const string BootstrapGlobal = "heimdallVncText";

    /// <summary>
    /// Every string the page shows, by the field name the page reads. A value the localizer could
    /// not give stays null and the page keeps what it has.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> Build(Func<string, string?> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [FieldDisconnected] = localize(KeyDisconnected),
            [FieldConnectionLost] = localize(KeyConnectionLost),
            [FieldInitFailed] = localize(KeyInitFailed),
            [FieldLoading] = localize(KeyLoading),
            [FieldLibraryFailedTitle] = localize(KeyLibraryFailedTitle),
            [FieldLibraryFailedMessage] = localize(KeyLibraryFailedMessage),
        };
    }

    /// <summary>
    /// The script that hands the strings to the page before its own scripts run. The default JSON
    /// encoder escapes markup and non-ASCII characters, so a translation cannot end the script.
    /// </summary>
    internal static string BuildBootstrapScript(Func<string, string?> localize)
    {
        return $"window.{BootstrapGlobal} = {JsonSerializer.Serialize(Build(localize))};";
    }
}
