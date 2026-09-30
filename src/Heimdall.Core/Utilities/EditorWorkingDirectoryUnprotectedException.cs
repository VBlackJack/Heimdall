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

namespace Heimdall.Core.Utilities;

/// <summary>
/// Raised when an editor working directory could not be restricted to the current user, so
/// nothing was staged in it.
/// </summary>
/// <remarks>
/// Carries a localization key rather than a message for the user: localized text is resolved by
/// the application, and the underlying failure stays in the log.
/// </remarks>
public sealed class EditorWorkingDirectoryUnprotectedException : IOException
{
    /// <summary>The localization key the application resolves for display.</summary>
    public const string LocaleKey = "ErrorEditorWorkingDirectoryUnprotected";

    /// <summary>
    /// Initializes a refusal caused by <paramref name="innerException"/>.
    /// </summary>
    /// <param name="innerException">Why the directory could not be restricted.</param>
    public EditorWorkingDirectoryUnprotectedException(Exception innerException)
        : base(
            "Refused to stage the file: the editor working directory could not be restricted to the current user.",
            innerException)
    {
    }

    /// <summary>Gets the localization key the application resolves for display.</summary>
    public string MessageKey => LocaleKey;
}
