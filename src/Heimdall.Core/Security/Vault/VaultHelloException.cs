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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Exception raised by the headless Windows Hello vault path. For cryptographic
/// failures the message is intentionally generic.
/// </summary>
public sealed class VaultHelloException : Exception
{
    /// <summary>Create an exception for a coarse failure reason.</summary>
    public VaultHelloException(VaultHelloFailureReason reason)
        : base(reason == VaultHelloFailureReason.CryptoFailure
            ? "Windows Hello vault unlock failed."
            : $"Windows Hello vault operation failed: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>The coarse, non-secret failure reason.</summary>
    public VaultHelloFailureReason Reason { get; }
}
