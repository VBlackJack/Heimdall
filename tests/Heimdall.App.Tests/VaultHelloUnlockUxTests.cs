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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

public sealed class VaultHelloUnlockUxTests
{
    [Theory]
    [InlineData(VaultHelloFailureReason.UserCanceled, VaultHelloUnlockUxAction.SilentFallback, null)]
    [InlineData(VaultHelloFailureReason.UserPrefersPassword, VaultHelloUnlockUxAction.SilentFallback, null)]
    [InlineData(VaultHelloFailureReason.SecurityDeviceLocked, VaultHelloUnlockUxAction.ShowMessage, "VaultHelloUnlockSecurityDeviceLocked")]
    [InlineData(VaultHelloFailureReason.NotFound, VaultHelloUnlockUxAction.TriggerReenroll, "VaultHelloUnlockNotFound")]
    [InlineData(VaultHelloFailureReason.Unavailable, VaultHelloUnlockUxAction.ShowMessage, "VaultHelloUnlockGenericFailure")]
    [InlineData(VaultHelloFailureReason.CryptoFailure, VaultHelloUnlockUxAction.ShowMessage, "VaultHelloUnlockGenericFailure")]
    public void Map_ReturnsExpectedActionAndMessage(
        VaultHelloFailureReason reason,
        VaultHelloUnlockUxAction expectedAction,
        string? expectedMessageKey)
    {
        var result = VaultHelloUnlockUx.Map(reason);

        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(expectedMessageKey, result.MessageKey);
    }
}
