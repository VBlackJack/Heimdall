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

namespace Heimdall.App.Tests;

public sealed class SessionLoggingResolverTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void ResolveSessionLogging_CombinesOverrideAndGlobal(
        bool? profileOverride,
        bool globalEnabled,
        bool expected)
    {
        SessionLoggingResolver.ResolveSessionLogging(profileOverride, globalEnabled)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void TranscriptGate_UsesResolvedSessionLogging(bool? profileOverride, bool globalEnabled, bool expected)
    {
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(profileOverride, globalEnabled);

        SessionLogGatePolicy.ShouldAutoStart(enabled, "SSH")
            .Should().Be(expected);
    }

    [Fact]
    public void TranscriptGate_OverrideOn_DoesNotForceWinRmAutoStart()
    {
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(profileOverride: true, globalEnabled: false);

        SessionLogGatePolicy.ShouldAutoStart(enabled, "WINRM")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void EventGate_UsesResolvedSessionLogging(bool? profileOverride, bool globalEnabled, bool expected)
    {
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(profileOverride, globalEnabled);

        SessionEventGatePolicy.ShouldLog(enabled, "RDP")
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void OperationGate_UsesResolvedSessionLogging(bool? profileOverride, bool globalEnabled, bool expected)
    {
        bool enabled = SessionLoggingResolver.ResolveSessionLogging(profileOverride, globalEnabled);

        SessionOperationGatePolicy.ShouldLog(enabled, "SFTP")
            .Should().Be(expected);
    }
}
