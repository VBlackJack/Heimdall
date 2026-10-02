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

using Heimdall.App.ViewModels.Session;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    /// <summary>
    /// The broadcast toggle's tooltip follows its scope label, whatever changed the label.
    /// </summary>
    /// <remarks>
    /// While broadcast is on, the tooltip embeds the scope label, and in SelectedPanes mode the
    /// label embeds the number of targeted panes. Selecting a pane or a whole tab raised the label
    /// and not the tooltip, so hovering the toggle showed the count from before; a scope set from
    /// anywhere but the cycle command did the same. The rule now lives in one place, and this
    /// drives it through the scope, the one entry a test can reach without a live terminal view.
    /// </remarks>
    [Fact]
    public void BroadcastToggleTooltip_IsNotifiedWheneverTheScopeLabelIs()
    {
        using TestHarness harness = TestHarness.Create();
        SessionCoordinator session = harness.Main.Session;
        session.IsBroadcastMode = true;

        var raised = new List<string?>();
        session.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.BroadcastScope = BroadcastScope.SelectedPanes;

        Assert.Contains(nameof(SessionCoordinator.BroadcastScopeLabel), raised);
        Assert.Contains(nameof(SessionCoordinator.BroadcastToggleTooltip), raised);
    }
}
