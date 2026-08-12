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

using System.Windows.Input;

namespace Heimdall.App.Tests;

public sealed class MainWindowTreePointerSelectionTests
{
    [Fact]
    public void CtrlShift_IsResolvedAsAdditiveBeforeCtrl()
    {
        (bool toggle, bool extend, bool additive) = MainWindow.ResolveTreePointerSelection(
            ModifierKeys.Control | ModifierKeys.Shift);

        Assert.False(toggle);
        Assert.False(extend);
        Assert.True(additive);
    }

    [Theory]
    [InlineData(ModifierKeys.Control, true, false)]
    [InlineData(ModifierKeys.Shift, false, true)]
    [InlineData(ModifierKeys.None, false, false)]
    public void OtherModifiers_PreserveExistingPointerDecisions(
        ModifierKeys modifiers,
        bool expectedToggle,
        bool expectedExtend)
    {
        (bool toggle, bool extend, bool additive) = MainWindow.ResolveTreePointerSelection(
            modifiers);

        Assert.Equal(expectedToggle, toggle);
        Assert.Equal(expectedExtend, extend);
        Assert.False(additive);
    }
}
