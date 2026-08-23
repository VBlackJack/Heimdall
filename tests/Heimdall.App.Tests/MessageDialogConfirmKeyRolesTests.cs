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
/// Which key does what on a two-button confirmation.
/// </summary>
/// <remarks>
/// The static XAML scan in <c>DialogKeyboardContractTests</c> checks that at most one
/// button carries each role in the markup. It cannot see a role moved at runtime, which
/// is the whole of what this decides.
/// <para>
/// The decision is pinned rather than its application to the buttons, because a test
/// cannot construct the dialog: a Window built on the shared test dispatcher seals
/// application-level styles onto that thread, and every later test that touches them
/// then fails on thread affinity - measured at 23 failures in one assembly.
/// </para>
/// </remarks>
public sealed class MessageDialogConfirmKeyRolesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmKeyRoles_EscapeAlwaysDeclines(bool primaryIsDefault)
    {
        MessageDialog.ConfirmKeyRoles roles =
            MessageDialog.DescribeConfirmKeyRoles(primaryIsDefault);

        // Escape declines whatever Enter does, and never lands on the collapsed button.
        Assert.True(roles.SecondaryIsCancel);
        Assert.False(roles.TertiaryIsCancel);
    }

    [Fact]
    public void ConfirmKeyRoles_OrdinaryQuestion_EnterAccepts()
    {
        MessageDialog.ConfirmKeyRoles roles =
            MessageDialog.DescribeConfirmKeyRoles(primaryIsDefault: true);

        Assert.True(roles.PrimaryIsDefault);
        Assert.False(roles.SecondaryIsDefault);
    }

    [Fact]
    public void ConfirmKeyRoles_DestructiveQuestion_EnterDeclinesLikeEscape()
    {
        MessageDialog.ConfirmKeyRoles roles =
            MessageDialog.DescribeConfirmKeyRoles(primaryIsDefault: false);

        // The point of the option. A user who has been pressing Enter at an unresponsive
        // surface must not drop a live connection by momentum.
        Assert.False(roles.PrimaryIsDefault);
        Assert.True(roles.SecondaryIsDefault);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmKeyRoles_ExactlyOneButtonIsTheDefault(bool primaryIsDefault)
    {
        MessageDialog.ConfirmKeyRoles roles =
            MessageDialog.DescribeConfirmKeyRoles(primaryIsDefault);

        // WPF allows one default per focus scope, so the two answers cannot agree.
        Assert.NotEqual(roles.PrimaryIsDefault, roles.SecondaryIsDefault);
    }
}
