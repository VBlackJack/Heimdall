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

namespace Heimdall.Sftp;

/// <summary>
/// The configured external editor could not be started.
/// </summary>
/// <remarks>
/// Raised after the edit session has been unregistered and its staged copy removed: a session
/// whose editor never started would otherwise watch, for the life of the pane, a file nobody
/// edits.
/// </remarks>
public sealed class ExternalEditorLaunchException : Exception
{
    public ExternalEditorLaunchException(string editorPath, Exception innerException)
        : base($"The external editor could not be started: {editorPath}", innerException)
    {
        EditorPath = editorPath;
    }

    /// <summary>The editor executable that failed to start.</summary>
    public string EditorPath { get; }
}
