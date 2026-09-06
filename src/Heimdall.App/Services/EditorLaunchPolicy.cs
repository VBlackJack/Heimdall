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

using System.Diagnostics;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Resolves the configured external editor and refuses a shell as an editor, for the local
/// browser and the remote one alike.
/// </summary>
/// <remarks>
/// The setting was wired to the local browser only: "Edit in external editor" on a remote file
/// always started notepad, and the shell-target refusal the local browser applied had no
/// counterpart on the remote side. One policy, two callers.
/// </remarks>
public static class EditorLaunchPolicy
{
    /// <summary>The editor used when none is configured.</summary>
    public const string DefaultEditorPath = @"%windir%\system32\notepad.exe";

    /// <summary>Locale key of the refusal shown when the configured editor is a shell.</summary>
    public const string ShellTargetRejectionKey = "EditorRejectedShellTarget";

    public static string ResolveEditorPath(string? configuredEditorPath)
    {
        string editorPath = string.IsNullOrWhiteSpace(configuredEditorPath)
            ? DefaultEditorPath
            : configuredEditorPath;
        return Environment.ExpandEnvironmentVariables(editorPath);
    }

    /// <summary>
    /// The editor to launch for the configured value. A shell target is refused: the default
    /// editor is returned and <paramref name="rejectionKey"/> names why.
    /// </summary>
    public static string ResolveExternalEditor(string? configuredEditorPath, out string? rejectionKey)
    {
        string editorPath = ResolveEditorPath(configuredEditorPath);
        if (InputValidator.IsShellTarget(editorPath))
        {
            rejectionKey = ShellTargetRejectionKey;
            return ResolveEditorPath(null);
        }

        rejectionKey = null;
        return editorPath;
    }

    public static bool TryCreateEditorStartInfo(
        string? configuredEditorPath,
        string filePath,
        out ProcessStartInfo? processStartInfo,
        out string? rejectionKey)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string editorPath = ResolveExternalEditor(configuredEditorPath, out rejectionKey);
        if (rejectionKey is not null)
        {
            processStartInfo = null;
            return false;
        }

        processStartInfo = CreateEditorStartInfo(editorPath, filePath);
        return true;
    }

    public static ProcessStartInfo CreateEditorStartInfo(string editorPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        ProcessStartInfo processStartInfo = new()
        {
            FileName = editorPath,
            UseShellExecute = false
        };
        processStartInfo.ArgumentList.Add(filePath);
        return processStartInfo;
    }
}
