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

using Heimdall.Core.Configuration;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultHelloUnlockOfferPolicyTests
{
    [Fact]
    public void ShouldOfferHelloUnlock_EnrolledAndNotDue_ReturnsTrue()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");
        var settings = new AppSettings
        {
            VaultEnabled = true,
            VaultHelloEnrolled = true,
            VaultHelloMaxDaysBeforeMasterPassword = 7,
            VaultLastMasterUnlockUtc = now.AddDays(-1)
        };

        Assert.True(VaultHelloUnlockOfferPolicy.ShouldOfferHelloUnlock(settings, now));
    }

    [Fact]
    public void ShouldOfferHelloUnlock_NotEnrolled_ReturnsFalse()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");
        var settings = new AppSettings
        {
            VaultEnabled = true,
            VaultHelloEnrolled = false
        };

        Assert.False(VaultHelloUnlockOfferPolicy.ShouldOfferHelloUnlock(settings, now));
    }

    [Fact]
    public void ShouldOfferHelloUnlock_ReauthDue_ReturnsFalse()
    {
        var now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");
        var settings = new AppSettings
        {
            VaultEnabled = true,
            VaultHelloEnrolled = true,
            VaultHelloMaxDaysBeforeMasterPassword = 7,
            VaultLastMasterUnlockUtc = now.AddDays(-8)
        };

        Assert.False(VaultHelloUnlockOfferPolicy.ShouldOfferHelloUnlock(settings, now));
    }
}
