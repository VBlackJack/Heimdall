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

namespace Heimdall.Core.Models;

/// <summary>
/// Determines which terminal panes receive broadcast (type-once, send-to-many)
/// input when broadcast mode is active.
/// </summary>
public enum BroadcastScope
{
    /// <summary>
    /// Only terminal panes within the active session tab receive the input.
    /// This is the default because it cannot reach panes the user is not
    /// looking at.
    /// </summary>
    CurrentTab,

    /// <summary>
    /// Terminal panes across every open session tab receive the input,
    /// including tabs in the background. This is the broad, riskier scope.
    /// </summary>
    AllTabs,

    /// <summary>
    /// Only the terminal panes the operator has marked receive the input, across every open
    /// session tab. An empty selection therefore reaches nothing rather than falling back to a
    /// wider scope.
    /// </summary>
    /// <remarks>
    /// This shipped. The comment here said it was reserved and resolved like
    /// <see cref="CurrentTab"/> long after <c>BroadcastTargetResolver</c> had begun spanning every
    /// tab for it, which is the opposite of what it does.
    /// </remarks>
    SelectedPanes
}
