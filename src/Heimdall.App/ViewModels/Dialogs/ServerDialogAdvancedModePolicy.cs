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

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Scopes the "open the server dialog in advanced mode" preference to the moments where it means
/// something. It answers when, never whether: a preference the dialog can overrule on a judgement
/// of its own is a preference the user cannot predict, and a checkbox that promises behaviour the
/// product does not deliver is worse than no checkbox at all.
/// </summary>
internal static class ServerDialogAdvancedModePolicy
{
    /// <summary>
    /// The advanced surface lives entirely in the RDP options, and in add mode it does not exist
    /// until a protocol has been picked, so applying the preference any earlier would settle the
    /// state of a control the dialog has not built yet.
    /// </summary>
    public static bool ShouldApplyRdpDefault(string? connectionType, bool isEditMode, bool isProtocolSelected)
    {
        return IsRdp(connectionType) && (isEditMode || isProtocolSelected);
    }

    private static bool IsRdp(string? connectionType)
    {
        return string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase);
    }
}
