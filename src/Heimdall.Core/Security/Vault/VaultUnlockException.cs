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
/// Thrown when the vault DEK cannot be unwrapped from its stored blob. A wrong
/// master password and a corrupted vault are deliberately indistinguishable:
/// the same type and the same generic message are used for every failure cause
/// (DPAPI failure, envelope decode failure, or AEAD tag mismatch), and no inner
/// exception or distinguishing detail is attached. This denies an attacker any
/// oracle that would separate "wrong password" from "tampered ciphertext".
/// </summary>
public sealed class VaultUnlockException : Exception
{
    /// <summary>The single message used for every unwrap failure.</summary>
    public const string GenericMessage = "The master password is incorrect or the vault is corrupted.";

    /// <summary>Create the exception with the fixed generic message.</summary>
    public VaultUnlockException()
        : base(GenericMessage)
    {
    }
}
