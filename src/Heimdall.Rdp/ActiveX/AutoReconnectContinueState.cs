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

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// What the control should do next, written back by the OnAutoReconnecting event handler.
/// </summary>
/// <remarks>
/// Mirrors the MsTscAx type library enum of the same shape, and the numbering is pinned against it
/// by MsTscAxEventContractTests. The values are what the control reads, so they are not free to
/// renumber.
/// </remarks>
public enum AutoReconnectContinueState
{
    /// <summary>
    /// Let the automatic reconnection continue. The control's default.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Stop reconnecting.
    /// </summary>
    Stop = 1,

    /// <summary>
    /// Hand the decision back to the user rather than retrying automatically.
    /// </summary>
    Manual = 2,
}
