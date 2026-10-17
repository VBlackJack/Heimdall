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

using System.IO;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// A question whose yes throws work away keeps it on Enter. The five editors below asked
/// "discard your changes?" (or "delete this macro?") with Enter on yes, so a user who pressed
/// Enter to close an editor lost what they had typed.
/// </summary>
public sealed class DestructiveConfirmDefaultTests
{
    /// <summary>The dialogs whose close path or delete button asks before throwing work away.</summary>
    public static TheoryData<string> DestructiveQuestionDialogs { get; } = new()
    {
        "CommandActionDialog.xaml.cs",
        "GatewayDialog.xaml.cs",
        "MacroEditorDialog.xaml.cs",
        "ScheduledTaskDialog.xaml.cs",
        "ServerDialog.xaml.cs",
    };

    [Fact]
    public void ADestructiveQuestionDeclinesOnEnter()
    {
        MessageDialog.ConfirmKeyRoles roles =
            MessageDialog.DescribeConfirmKeyRoles(MessageDialog.DestructivePrimaryIsDefault);

        Assert.False(roles.PrimaryIsDefault);
        Assert.True(roles.SecondaryIsDefault);
        Assert.True(roles.SecondaryIsCancel);
    }

    [Theory]
    [MemberData(nameof(DestructiveQuestionDialogs))]
    public void TheDialogAsksThroughTheDestructiveConfirmation(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", fileName));

        // The plain overload puts Enter on yes, and each of these files asks only questions
        // whose yes throws work away.
        Assert.DoesNotContain("MessageDialog.ShowConfirm(", source, StringComparison.Ordinal);
    }
}
