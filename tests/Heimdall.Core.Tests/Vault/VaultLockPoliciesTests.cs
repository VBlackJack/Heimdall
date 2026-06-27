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

public sealed class VaultLockPoliciesTests
{
    [Theory]
    [InlineData(0, 5)]        // no idle yet
    [InlineData(299_000, 5)]  // 4m59s < 5m
    public void ShouldAutoLock_BelowThreshold_False(long idleMs, int minutes)
    {
        Assert.False(VaultIdlePolicy.ShouldAutoLock(idleMs, minutes));
    }

    [Theory]
    [InlineData(300_000, 5)]  // exactly 5m
    [InlineData(600_000, 5)]  // 10m
    [InlineData(60_000, 1)]   // 1m at 1m
    public void ShouldAutoLock_AtOrAboveThreshold_True(long idleMs, int minutes)
    {
        Assert.True(VaultIdlePolicy.ShouldAutoLock(idleMs, minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ShouldAutoLock_DisabledThreshold_NeverLocks(int minutes)
    {
        Assert.False(VaultIdlePolicy.ShouldAutoLock(long.MaxValue, minutes));
    }

    [Fact]
    public void ShouldDeferReconnect_Locked_True()
    {
        Assert.True(VaultReconnectPolicy.ShouldDeferReconnect(isWorkspaceLocked: true));
    }

    [Fact]
    public void ShouldDeferReconnect_Unlocked_False()
    {
        Assert.False(VaultReconnectPolicy.ShouldDeferReconnect(isWorkspaceLocked: false));
    }
}
