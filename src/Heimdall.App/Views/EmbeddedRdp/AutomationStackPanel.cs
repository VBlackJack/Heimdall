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
/// A <see cref="StackPanel"/> that appears in the UI Automation tree.
/// </summary>
/// <remarks>
/// Same reason as <see cref="AutomationBorder"/>: a panel has no automation peer, so the health dot
/// and the connection-phase stepper were computing announcement strings that no UIA client could
/// ever read.
/// </remarks>
public sealed class AutomationStackPanel : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
