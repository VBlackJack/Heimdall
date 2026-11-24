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

using System.Windows;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.UiTests;

/// <summary>
/// The large layout is an opt-in: the default dialog keeps the size its markup declares, and the large one
/// is wider and taller but never beyond the work area it opens on.
/// </summary>
[Collection(DesktopUiCollection.Name)]
[Trait("Category", "RequiresDesktop")]
public sealed class MessageDialogLargeLayoutTests
{
    private const double DefaultWidth = 460;
    private const double DefaultMaxHeight = 500;

    [StaFact]
    public void TheDefaultDialogKeepsItsMarkupSize()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                Assert.Equal(DefaultWidth, dialog.Width);
                Assert.Equal(DefaultMaxHeight, dialog.MaxHeight);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaFact]
    public void TheLargeDialogIsWiderAndTallerAndStaysInsideTheWorkArea()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                Rect workArea = new(0, 0, 1920, 1040);

                dialog.UseLargeLayout(workArea);

                Assert.Equal(MessageDialog.LargeWidth, dialog.Width);
                Assert.True(dialog.Width > DefaultWidth);
                Assert.True(dialog.MaxHeight > DefaultMaxHeight);
                Assert.True(dialog.MaxHeight <= workArea.Height);
                Assert.True(dialog.Width <= workArea.Width);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaFact]
    public void OnASmallWorkAreaTheLargeDialogIsClampedToIt()
    {
        WpfTestHost.Invoke(() =>
        {
            var dialog = new MessageDialog();
            try
            {
                Rect workArea = new(0, 0, 600, 400);

                dialog.UseLargeLayout(workArea);

                Assert.Equal(workArea.Width, dialog.Width);
                Assert.True(dialog.MaxHeight <= workArea.Height);
                Assert.True(dialog.MaxHeight < DefaultMaxHeight);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [StaFact]
    public void TheWorkAreaWithoutAnOwnerIsThePrimaryScreens()
    {
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(SystemParameters.WorkArea, MessageDialog.WorkAreaOf(null));
        });
    }
}
