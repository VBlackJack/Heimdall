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

public sealed class UpdateInstallOutcomeTextTests
{
    [Fact]
    public void StatusKey_Started_ReturnsNull()
    {
        UpdateInstallOutcomeText.StatusKey(UpdateInstallOutcome.Started).Should().BeNull();
    }

    [Theory]
    [InlineData(UpdateInstallOutcome.InstallLaunchFailed, "SettingsUpdateStatusInstallFailed")]
    [InlineData(UpdateInstallOutcome.Cancelled, "SettingsUpdateStatusCancelled")]
    [InlineData(UpdateInstallOutcome.VerificationFailed, "SettingsUpdateStatusVerificationFailed")]
    [InlineData(UpdateInstallOutcome.DownloadFailed, "SettingsUpdateStatusDownloadFailed")]
    public void StatusKey_NonSuccessOutcome_MapsToExpectedKey(UpdateInstallOutcome outcome, string expectedKey)
    {
        UpdateInstallOutcomeText.StatusKey(outcome).Should().Be(expectedKey);
    }
}
