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

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// A <see cref="Border"/> that appears in the UI Automation tree.
/// </summary>
/// <remarks>
/// <para><see cref="Border"/> does not override <see cref="System.Windows.UIElement.OnCreateAutomationPeer"/>,
/// so it has no automation peer at all: a UIA client walking the tree descends straight past it and
/// every <c>AutomationProperties</c> value set on it - a name, a live setting, a dialog flag - is
/// never read by anything. Overriding the peer is the only way to correct that, because
/// <c>OnCreateAutomationPeer</c> is protected virtual and there is no attached-property route to
/// it.</para>
/// <para>Used for the RDP view's live regions and for the reconnect overlay, which declares itself
/// a dialog.</para>
/// </remarks>
public sealed class AutomationBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
