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
/// Assigns the exact pane that owns a hosted session surface.
/// </summary>
/// <remarks>
/// <see cref="SessionPaneModel"/> calls it whenever a host is placed on a pane, so a host always
/// knows the pane it lives in, wherever a swap, a merge or a detach has moved that pane.
/// </remarks>
public interface ISessionPaneOwner
{
    /// <summary>
    /// Associates the host with the pane whose runtime lifecycle it represents.
    /// </summary>
    void SetOwningPane(SessionPaneModel pane);
}
