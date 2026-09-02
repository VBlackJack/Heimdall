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
/// Pure strength policy for the vault master password. The master password
/// protects the entire credential store, so the floor is stricter than the
/// per-connection password fields.
/// </summary>
/// <remarks>
/// Thresholds follow ANSSI/NIST guidance: a 12-character minimum with at least
/// three of the four character classes, OR a 20-character minimum that exempts
/// complexity (a long passphrase compensates for variety). All thresholds are
/// named constants - no inline magic numbers.
/// </remarks>
public static class MasterPasswordPolicy
{
    /// <summary>Absolute minimum length for any accepted master password.</summary>
    public const int MinLength = 12;

    /// <summary>Minimum number of character classes required below the passphrase length.</summary>
    public const int MinCharacterClasses = 3;

    /// <summary>Length at or above which the character-class requirement is waived.</summary>
    public const int PassphraseExemptionLength = 20;

    /// <summary>
    /// Validate a candidate master password against the strength policy.
    /// </summary>
    /// <param name="password">The candidate password characters.</param>
    /// <returns>An acceptable result, or a result carrying the first failure reason.</returns>
    public static MasterPasswordPolicyResult Validate(ReadOnlySpan<char> password)
    {
        if (password.Length < MinLength)
        {
            return new MasterPasswordPolicyResult(false, MasterPasswordPolicyError.TooShort);
        }

        if (password.Length >= PassphraseExemptionLength)
        {
            return new MasterPasswordPolicyResult(true, null);
        }

        if (CountCharacterClasses(password) < MinCharacterClasses)
        {
            return new MasterPasswordPolicyResult(false, MasterPasswordPolicyError.InsufficientComplexity);
        }

        return new MasterPasswordPolicyResult(true, null);
    }

    private static int CountCharacterClasses(ReadOnlySpan<char> password)
    {
        bool lower = false, upper = false, digit = false, other = false;

        foreach (var c in password)
        {
            if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else
            {
                other = true;
            }
        }

        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
    }
}

/// <summary>
/// Outcome of <see cref="MasterPasswordPolicy.Validate"/>.
/// </summary>
/// <param name="IsAcceptable">True when the password meets the policy.</param>
/// <param name="Error">The first failed rule, or null when acceptable.</param>
public readonly record struct MasterPasswordPolicyResult(bool IsAcceptable, MasterPasswordPolicyError? Error);

/// <summary>
/// Reasons a master password can be rejected by <see cref="MasterPasswordPolicy"/>.
/// </summary>
public enum MasterPasswordPolicyError
{
    /// <summary>Shorter than <see cref="MasterPasswordPolicy.MinLength"/>.</summary>
    TooShort,

    /// <summary>Fewer than <see cref="MasterPasswordPolicy.MinCharacterClasses"/> classes and below the passphrase length.</summary>
    InsufficientComplexity,
}
