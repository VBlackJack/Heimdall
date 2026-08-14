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

using Heimdall.App.Services;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Decides whether the control must scale its remote surface for a given resolution choice. Pure,
/// so the decision is testable without a live ActiveX control.
/// </summary>
/// <remarks>
/// SmartSizing lives on the native control and survives a resolution change, so every choice has
/// to state its value rather than only turning it on. Setting it in one branch and leaving it
/// alone in the other lets the previous mode leak: after a Fit-to-Window, a fixed preset that fits
/// the surface would stay stretched, and on a profile that starts with scaling disabled a switch
/// to Fit-to-Window would stay unscaled.
/// </remarks>
internal static class RdpSmartSizingPolicy
{
    /// <param name="kind">The resolution choice being applied.</param>
    /// <param name="resolutionExceedsSurface">
    /// Whether the chosen resolution is larger than the surface hosting it. Only meaningful for a
    /// fixed choice; Fit-to-Window scales whatever its size.
    /// </param>
    internal static bool ShouldEnable(ResolutionChoiceKind kind, bool resolutionExceedsSurface)
        => kind switch
        {
            ResolutionChoiceKind.MatchWindow => true,
            _ => resolutionExceedsSurface,
        };
}
