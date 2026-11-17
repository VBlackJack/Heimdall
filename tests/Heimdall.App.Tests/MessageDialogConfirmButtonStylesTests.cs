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

using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// The accent of a confirmation follows the button Enter presses.
/// </summary>
public sealed class MessageDialogConfirmButtonStylesTests
{
    [Fact]
    public void PrimaryIsDefault_AcceptWearsTheAccent()
    {
        MessageDialog.ConfirmButtonStyles styles = MessageDialog.DescribeConfirmButtonStyles(primaryIsDefault: true);

        Assert.Equal(MessageDialog.AccentButtonStyleKey, styles.PrimaryStyleKey);
        Assert.Equal(MessageDialog.PlainButtonStyleKey, styles.SecondaryStyleKey);
    }

    [Fact]
    public void DeclineIsDefault_DeclineWearsTheAccent()
    {
        MessageDialog.ConfirmButtonStyles styles = MessageDialog.DescribeConfirmButtonStyles(primaryIsDefault: false);

        Assert.Equal(MessageDialog.PlainButtonStyleKey, styles.PrimaryStyleKey);
        Assert.Equal(MessageDialog.AccentButtonStyleKey, styles.SecondaryStyleKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactlyOneButtonWearsTheAccent(bool primaryIsDefault)
    {
        MessageDialog.ConfirmButtonStyles styles = MessageDialog.DescribeConfirmButtonStyles(primaryIsDefault);

        Assert.NotEqual(styles.PrimaryStyleKey, styles.SecondaryStyleKey);
    }
}
