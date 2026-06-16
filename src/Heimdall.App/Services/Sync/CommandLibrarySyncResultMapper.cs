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

using TwinShell.Core.Interfaces;

namespace Heimdall.App.Services.Sync;

/// <summary>
/// Stateless mapper turning a <see cref="GitOperationResult"/> into the dialog
/// presentation (kind, title, body) for the Command Library sync flow. Mirrors
/// the original inline logic of <c>CommandLibraryViewModel.SyncAsync</c> exactly.
/// </summary>
public static class CommandLibrarySyncResultMapper
{
    /// <summary>
    /// Maps a Git-sync result to its dialog presentation, resolving localized
    /// fallback text through <paramref name="localize"/>.
    /// </summary>
    public static SyncResultPresentation Map(GitOperationResult result, Func<string, string> localize)
    {
        if (result.ErrorCode == GitSyncErrorCode.Cancelled)
        {
            var body = string.IsNullOrWhiteSpace(result.Message)
                ? localize("ToolCmdLibSyncCancelledMessage")
                : result.Message;
            return new SyncResultPresentation(
                SyncDialogKind.Info,
                localize("ToolCmdLibSyncCancelled"),
                body);
        }

        if (result.Success)
        {
            var hasWarnings = result.Warnings?.Count > 0;
            if (hasWarnings)
            {
                return new SyncResultPresentation(
                    SyncDialogKind.Warning,
                    localize("ToolCmdLibSyncPartial"),
                    result.Message ?? localize("ToolCmdLibSyncComplete"));
            }

            return new SyncResultPresentation(
                SyncDialogKind.Info,
                localize("ToolCmdLibSyncComplete"),
                result.Message ?? localize("ToolCmdLibSyncComplete"));
        }

        return new SyncResultPresentation(
            SyncDialogKind.Error,
            localize("ToolCmdLibSyncError"),
            result.ErrorDetails ?? result.Message ?? localize("ToolCmdLibSyncError"));
    }
}
