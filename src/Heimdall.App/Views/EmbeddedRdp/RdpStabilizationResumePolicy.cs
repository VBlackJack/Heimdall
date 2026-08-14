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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>What the view must do when the post-connect stabilization lockout ends.</summary>
internal enum RdpStabilizationResumeAction
{
    /// <summary>Nothing to replay: no DPI change was dropped and the queued size is already applied.</summary>
    None,

    /// <summary>Apply the queued size, which differs from the one currently applied.</summary>
    ApplyQueued,

    /// <summary>
    /// Re-apply unconditionally, bypassing the unchanged-size shortcut, because a DPI change was
    /// dropped during the lockout.
    /// </summary>
    ApplyForced,
}

/// <summary>
/// Decides what the end of the stabilization lockout owes the session. Pure, so the decision is
/// testable without a live ActiveX control.
/// </summary>
internal static class RdpStabilizationResumePolicy
{
    /// <param name="dpiChangeDropped">A DPI change arrived during the lockout and was discarded.</param>
    /// <param name="queuedWidth">Width the session would use now.</param>
    /// <param name="queuedHeight">Height the session would use now.</param>
    /// <param name="lastAppliedWidth">Width last pushed to the control.</param>
    /// <param name="lastAppliedHeight">Height last pushed to the control.</param>
    /// <remarks>
    /// A dropped DPI change forces the apply rather than merely queueing it: moving to a monitor of
    /// a different scale can leave the pixel dimensions identical while the scale factors change,
    /// so a comparison against the last applied size would conclude there is nothing to do and the
    /// session would stay at the old scale until some later, unrelated event.
    /// </remarks>
    internal static RdpStabilizationResumeAction Decide(
        bool dpiChangeDropped,
        int queuedWidth,
        int queuedHeight,
        int lastAppliedWidth,
        int lastAppliedHeight)
    {
        if (dpiChangeDropped)
        {
            return RdpStabilizationResumeAction.ApplyForced;
        }

        if (queuedWidth <= 0 || queuedHeight <= 0)
        {
            return RdpStabilizationResumeAction.None;
        }

        return queuedWidth != lastAppliedWidth || queuedHeight != lastAppliedHeight
            ? RdpStabilizationResumeAction.ApplyQueued
            : RdpStabilizationResumeAction.None;
    }
}
