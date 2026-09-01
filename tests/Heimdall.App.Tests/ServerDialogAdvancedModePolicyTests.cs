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

using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Tests;

public sealed class ServerDialogAdvancedModePolicyTests
{
    // The advanced surface is RDP-only, and in add mode the dialog has not built it until a
    // protocol is picked. Applying the preference outside those moments would settle the state
    // of a control that is not on screen, and in add mode it would do so while the user is
    // still looking at the protocol chooser.
    [Theory]
    [InlineData("RDP", true, false, true)]
    [InlineData("RDP", false, true, true)]
    [InlineData("RDP", false, false, false)]
    [InlineData("SSH", true, true, false)]
    [InlineData("Telnet", false, true, false)]
    [InlineData(null, true, true, false)]
    public void ShouldApplyRdpDefault_OnlyOnceAnRdpFormIsOnScreen(
        string? connectionType,
        bool isEditMode,
        bool isProtocolSelected,
        bool expected)
    {
        bool actual = ServerDialogAdvancedModePolicy.ShouldApplyRdpDefault(
            connectionType,
            isEditMode,
            isProtocolSelected);

        Assert.Equal(expected, actual);
    }
}
