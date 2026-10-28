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

using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The macro editor dialog cannot be built without a localizer.
/// </summary>
/// <remarks>
/// It used to take an optional one and answer a missing one with English sentences written in the
/// code, which no caller ever reached and which drifted from the catalogue wording.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class MacroEditorDialogLocalizerTests
{
    [StaFact]
    public void TheDialog_RefusesToBeBuiltWithoutALocalizer()
    {
        WpfTestHost.Invoke(() =>
        {
            Assert.Throws<ArgumentNullException>(() => new MacroEditorDialog(null!));
        });
    }
}
