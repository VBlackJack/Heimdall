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

using FluentAssertions;
using Heimdall.App.Services;
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

/// <summary>
/// The dangerous-command question names its two answers after the acts and declines by default,
/// and keeps rendering above the Ctrl+K palette.
/// </summary>
public sealed class DangerousCommandGuardLabeledConfirmTests
{
    private static readonly Func<string, string> IdentityLocalizer = key => key;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmIfDangerousAsync_Dangerous_AsksTheLabeledQuestionAndPassesTopmostThrough(bool topmost)
    {
        SixArgConfirmDialogService dialog = new() { Answer = true };

        bool result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Dangerous, dialog, IdentityLocalizer, topmost);

        result.Should().BeTrue();
        dialog.PlainCallCount.Should().Be(0);
        LabeledConfirmCall call = dialog.LabeledCalls.Should().ContainSingle().Subject;
        call.Title.Should().Be("ConfirmDangerousSendTitle");
        call.Message.Should().Be("ConfirmDangerousSendMessage");
        call.Severity.Should().Be("warning");
        call.ConfirmLabel.Should().Be("ConfirmDangerousSendAccept");
        call.DeclineLabel.Should().Be("ConfirmDangerousSendDecline");
        call.Topmost.Should().Be(topmost);
    }

    [Fact]
    public async Task ConfirmIfDangerousAsync_Declined_ReturnsFalse()
    {
        SixArgConfirmDialogService dialog = new() { Answer = false };

        bool result = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            CriticalityLevel.Dangerous, dialog, IdentityLocalizer, topmost: true);

        result.Should().BeFalse();
        dialog.LabeledCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task SixArgConfirm_DefaultMember_ForwardsToTheLabeledFiveArgOverload()
    {
        FiveArgConfirmDialogService fake = new() { Answer = true };
        IDialogService dialog = fake;

        bool result = await dialog.ShowConfirmAsync("t", "m", "danger", "yes label", "no label", topmost: true);

        result.Should().BeTrue();
        fake.PlainCallCount.Should().Be(0);
        fake.LabeledCalls.Should().ContainSingle().Which.Should()
            .Be(new LabeledConfirmCall("t", "m", "danger", "yes label", "no label", Topmost: false));
    }
}
