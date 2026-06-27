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
/// Thrown by the vault lifecycle when a proposed master password is rejected by
/// <see cref="MasterPasswordPolicy"/>. Carries the structured reason so the UI
/// (a later lot) can render a localized message; the exception message itself is
/// generic and contains no password material.
/// </summary>
public sealed class MasterPasswordPolicyException : Exception
{
    /// <summary>The structured policy failure reason.</summary>
    public MasterPasswordPolicyError Error { get; }

    /// <summary>Create the exception for a specific policy failure.</summary>
    /// <param name="error">The rule that failed.</param>
    public MasterPasswordPolicyException(MasterPasswordPolicyError error)
        : base("The proposed master password does not meet the strength policy.")
    {
        Error = error;
    }
}
