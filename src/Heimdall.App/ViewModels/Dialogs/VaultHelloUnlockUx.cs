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

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>UI action to take after a failed Windows Hello vault unlock.</summary>
public enum VaultHelloUnlockUxAction
{
    /// <summary>No visible response.</summary>
    None,

    /// <summary>Return silently to the master-password prompt.</summary>
    SilentFallback,

    /// <summary>Show a localized message and keep the master-password prompt focused.</summary>
    ShowMessage,

    /// <summary>Credential is gone; request master password, then offer re-enrollment.</summary>
    TriggerReenroll,
}

/// <summary>Localized UX mapping for a Windows Hello unlock failure.</summary>
public sealed record VaultHelloUnlockUxResult(VaultHelloUnlockUxAction Action, string? MessageKey);

/// <summary>Pure mapper from coarse Hello failure reasons to user-facing behavior.</summary>
public static class VaultHelloUnlockUx
{
    /// <summary>Map a non-secret Hello failure reason to a UI action and locale key.</summary>
    public static VaultHelloUnlockUxResult Map(VaultHelloFailureReason? reason)
    {
        return reason switch
        {
            VaultHelloFailureReason.UserCanceled or VaultHelloFailureReason.UserPrefersPassword =>
                new VaultHelloUnlockUxResult(VaultHelloUnlockUxAction.SilentFallback, null),
            VaultHelloFailureReason.SecurityDeviceLocked =>
                new VaultHelloUnlockUxResult(
                    VaultHelloUnlockUxAction.ShowMessage,
                    "VaultHelloUnlockSecurityDeviceLocked"),
            VaultHelloFailureReason.NotFound =>
                new VaultHelloUnlockUxResult(
                    VaultHelloUnlockUxAction.TriggerReenroll,
                    "VaultHelloUnlockNotFound"),
            VaultHelloFailureReason.Unavailable or VaultHelloFailureReason.CryptoFailure or null =>
                new VaultHelloUnlockUxResult(
                    VaultHelloUnlockUxAction.ShowMessage,
                    "VaultHelloUnlockGenericFailure"),
            _ => new VaultHelloUnlockUxResult(
                VaultHelloUnlockUxAction.ShowMessage,
                "VaultHelloUnlockGenericFailure"),
        };
    }
}
