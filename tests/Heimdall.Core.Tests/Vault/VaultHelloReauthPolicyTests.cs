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

using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultHelloReauthPolicyTests
{
    [Fact]
    public void ShouldRequireMasterPassword_Off_NeverRequires()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");

        Assert.False(VaultHelloReauthPolicy.ShouldRequireMasterPassword(null, 0, now));
        Assert.False(VaultHelloReauthPolicy.ShouldRequireMasterPassword(now.AddYears(-10), 0, now));
    }

    [Fact]
    public void ShouldRequireMasterPassword_PastMaxDays_Requires()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");

        Assert.True(VaultHelloReauthPolicy.ShouldRequireMasterPassword(now.AddDays(-8), 7, now));
    }

    [Fact]
    public void ShouldRequireMasterPassword_WithinMaxDays_DoesNotRequire()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");

        Assert.False(VaultHelloReauthPolicy.ShouldRequireMasterPassword(now.AddDays(-6), 7, now));
    }
}
