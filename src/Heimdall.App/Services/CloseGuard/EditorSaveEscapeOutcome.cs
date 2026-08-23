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

using Heimdall.App.Views;

namespace Heimdall.App.Services.CloseGuard;

/// <summary>
/// What the editor overlay should do when its Close button is pressed during a save,
/// and what it should say afterwards.
/// </summary>
/// <remarks>
/// A separate, WPF-free class because this is the part worth testing: the view is a
/// <c>UserControl</c> that cannot be built without a desktop, and its constructor is
/// pinned by reflection on an exact parameter list, so the decision cannot take an
/// injected clock and live there.
/// </remarks>
public static class EditorSaveEscapeOutcome
{
    /// <summary>What the overlay's Close button should do this time.</summary>
    public enum Response
    {
        /// <summary>Nothing is in flight; the ordinary close may proceed.</summary>
        AllowClose,

        /// <summary>Offer to drop the connection.</summary>
        OfferEscape,

        /// <summary>An escape was already attempted; report how it went instead.</summary>
        ReportOutcome,
    }

    /// <summary>Decides what the Close press means.</summary>
    /// <param name="saveInProgress">Whether the editor is still writing back.</param>
    /// <param name="escapeAttempted">Whether the connection was already dropped once.</param>
    /// <remarks>
    /// Re-offering a drop on an already-dropped connection is the shape this exists to
    /// prevent: the second press would raise the same question about an act that has
    /// already happened, and consenting again would do nothing while looking like it did
    /// something.
    /// </remarks>
    public static Response Decide(bool saveInProgress, bool escapeAttempted)
    {
        if (!saveInProgress)
        {
            return Response.AllowClose;
        }

        return escapeAttempted ? Response.ReportOutcome : Response.OfferEscape;
    }

    /// <summary>Which sentence describes how the escape went.</summary>
    /// <param name="saveInProgress">Whether the save flag is still set.</param>
    /// <remarks>
    /// The flag is the observable, deliberately - not the disconnect task. On a stalled
    /// send, dropping the connection can itself block, so its task may never complete;
    /// awaiting that would leave the escape unreported, which is the same defect one
    /// level up from the one this whole item exists to fix.
    /// </remarks>
    public static string ReportKey(bool saveInProgress) =>
        saveInProgress
            ? SftpCloseGuardLocaleKeys.EditorSaveEscapeStuck
            : SftpCloseGuardLocaleKeys.EditorSaveEscapeDropped;
}
