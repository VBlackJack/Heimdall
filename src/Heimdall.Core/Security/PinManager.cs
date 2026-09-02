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

using System.Security.Cryptography;
using System.Text;

namespace Heimdall.Core.Security;

/// <summary>
/// Manages PIN-based authentication with PBKDF2-SHA256 hashing (ANSSI-002 compliant)
/// and brute-force lockout protection (ANSSI-004 compliant).
/// </summary>
public sealed class PinManager
{
    /// <summary>PBKDF2 iteration count (ANSSI-002).</summary>
    private const int PbkdfIterations = 100_000;

    /// <summary>Derived key length in bytes (256-bit).</summary>
    private const int PbkdfKeyLengthBytes = 32;

    /// <summary>Salt length in bytes (128-bit).</summary>
    private const int SaltLengthBytes = 16;

    /// <summary>Minimum PIN length (inclusive).</summary>
    private const int MinPinLength = 4;

    /// <summary>Maximum PIN length (inclusive).</summary>
    private const int MaxPinLength = 8;

    /// <summary>Default lockout duration.</summary>
    private static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private int _failureCount;
    private DateTime? _lockoutUntilUtc;

    /// <summary>
    /// Maximum allowed PIN attempts before lockout (default: 5).
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// Duration of lockout after exceeding <see cref="MaxAttempts"/>.
    /// </summary>
    public TimeSpan LockoutDuration { get; }

    /// <summary>
    /// Current number of consecutive failed PIN attempts.
    /// </summary>
    public int FailureCount
    {
        get { lock (_lock) return _failureCount; }
    }

    /// <summary>
    /// Absolute UTC instant until which the PIN is locked out, or null when not locked out. Exposed for persistence.
    /// </summary>
    public DateTime? LockoutUntilUtc
    {
        get { lock (_lock) return _lockoutUntilUtc; }
    }

    /// <summary>
    /// Raised whenever the persisted lockout state (failure count or lockout expiry) changes, so the host can persist it.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Whether the PIN is currently locked out due to too many failures.
    /// </summary>
    public bool IsLockedOut
    {
        get
        {
            lock (_lock)
            {
                if (_lockoutUntilUtc is null)
                    return false;

                if (DateTime.UtcNow >= _lockoutUntilUtc.Value)
                {
                    // Lockout expired - auto-reset
                    // Intentionally no StateChanged here; persisted expiry is reconciled during restore.
                    _failureCount = 0;
                    _lockoutUntilUtc = null;
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Remaining lockout time. Returns <see cref="TimeSpan.Zero"/> when not locked out.
    /// </summary>
    public TimeSpan LockoutRemaining
    {
        get
        {
            lock (_lock)
            {
                if (_lockoutUntilUtc is null)
                    return TimeSpan.Zero;

                var remaining = _lockoutUntilUtc.Value - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Create a new PinManager with the specified lockout parameters.
    /// </summary>
    /// <param name="maxAttempts">Maximum failed attempts before lockout (default: 5).</param>
    /// <param name="lockoutDuration">Lockout duration (default: 5 minutes).</param>
    public PinManager(int maxAttempts = 5, TimeSpan? lockoutDuration = null)
    {
        MaxAttempts = maxAttempts > 0 ? maxAttempts : 5;
        LockoutDuration = lockoutDuration ?? DefaultLockoutDuration;
    }

    /// <summary>
    /// Hash a PIN using PBKDF2-SHA256 with the given salt.
    /// </summary>
    /// <param name="pin">The PIN to hash.</param>
    /// <param name="salt">Base64-encoded 128-bit salt.</param>
    /// <returns>Base64-encoded 256-bit derived key.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when salt length is not 16 bytes.</exception>
    public string Hash(string pin, string salt)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(salt);

        byte[]? pinBytes = null;
        byte[]? saltBytes = null;
        byte[]? hash = null;

        try
        {
            saltBytes = Convert.FromBase64String(salt);
            if (saltBytes.Length != SaltLengthBytes)
            {
                throw new ArgumentException(
                    $"Invalid salt length: expected {SaltLengthBytes} bytes, got {saltBytes.Length}.");
            }

            pinBytes = Encoding.UTF8.GetBytes(pin);

            hash = Rfc2898DeriveBytes.Pbkdf2(
                pinBytes,
                saltBytes,
                PbkdfIterations,
                HashAlgorithmName.SHA256,
                PbkdfKeyLengthBytes);

            return Convert.ToBase64String(hash);
        }
        finally
        {
            if (pinBytes is not null) Array.Clear(pinBytes);
            if (saltBytes is not null) Array.Clear(saltBytes);
            if (hash is not null) Array.Clear(hash);
        }
    }

    /// <summary>
    /// Generate a cryptographically secure random salt.
    /// </summary>
    /// <returns>Base64-encoded 128-bit salt.</returns>
    public string GenerateSalt()
    {
        var saltBytes = new byte[SaltLengthBytes];

        try
        {
            RandomNumberGenerator.Fill(saltBytes);
            return Convert.ToBase64String(saltBytes);
        }
        finally
        {
            Array.Clear(saltBytes);
        }
    }

    /// <summary>
    /// Verify a PIN against a stored hash and salt using constant-time comparison
    /// to prevent timing attacks (CWE-208 prevention).
    /// </summary>
    /// <param name="pin">The PIN to verify.</param>
    /// <param name="storedHash">The stored Base64-encoded hash.</param>
    /// <param name="salt">The stored Base64-encoded salt.</param>
    /// <returns>True if the PIN matches.</returns>
    public bool Verify(string pin, string storedHash, string salt)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(storedHash);
        ArgumentNullException.ThrowIfNull(salt);

        byte[]? inputHashBytes = null;
        byte[]? storedHashBytes = null;

        try
        {
            var inputHash = Hash(pin, salt);
            inputHashBytes = Convert.FromBase64String(inputHash);
            storedHashBytes = Convert.FromBase64String(storedHash);

            return CryptographicOperations.FixedTimeEquals(inputHashBytes, storedHashBytes);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (inputHashBytes is not null) Array.Clear(inputHashBytes);
            if (storedHashBytes is not null) Array.Clear(storedHashBytes);
        }
    }

    /// <summary>
    /// Validate PIN format: must be 4-8 digits only.
    /// </summary>
    /// <param name="pin">The PIN to validate.</param>
    /// <returns>A validation result with status and error detail.</returns>
    public PinValidationResult ValidateFormat(string pin)
    {
        if (string.IsNullOrEmpty(pin))
            return new PinValidationResult(false, PinValidationError.TooShort);

        if (pin.Length < MinPinLength)
            return new PinValidationResult(false, PinValidationError.TooShort);

        if (pin.Length > MaxPinLength)
            return new PinValidationResult(false, PinValidationError.TooLong);

        foreach (var c in pin)
        {
            if (!char.IsAsciiDigit(c))
                return new PinValidationResult(false, PinValidationError.InvalidChars);
        }

        return new PinValidationResult(true, null);
    }

    /// <summary>
    /// Register a failed PIN attempt. Triggers lockout when <see cref="MaxAttempts"/> is reached.
    /// </summary>
    /// <returns>The updated failure count.</returns>
    public int RegisterFailure()
    {
        int failureCount;

        lock (_lock)
        {
            _failureCount++;

            if (_failureCount >= MaxAttempts)
            {
                _lockoutUntilUtc = DateTime.UtcNow.Add(LockoutDuration);
            }

            failureCount = _failureCount;
        }

        StateChanged?.Invoke();
        return failureCount;
    }

    /// <summary>
    /// Reset the failure counter after a successful authentication.
    /// </summary>
    public void ResetFailures()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _lockoutUntilUtc = null;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Restore lockout state from persisted storage (e.g., after application restart).
    /// </summary>
    /// <param name="failureCount">The persisted failure count.</param>
    /// <param name="lockoutUntilUtc">The persisted lockout expiry (UTC), or null.</param>
    public void RestoreLockoutState(int failureCount, DateTime? lockoutUntilUtc)
    {
        lock (_lock)
        {
            _failureCount = Math.Max(0, failureCount);

            if (lockoutUntilUtc.HasValue && DateTime.UtcNow < lockoutUntilUtc.Value)
            {
                _lockoutUntilUtc = lockoutUntilUtc.Value;
            }
            else
            {
                // Expired lockout - reset
                _lockoutUntilUtc = null;
                if (lockoutUntilUtc.HasValue)
                {
                    _failureCount = 0;
                }
            }
        }
    }
}

/// <summary>
/// Result of a PIN format validation.
/// </summary>
/// <param name="IsValid">True if the PIN format is acceptable.</param>
/// <param name="Error">The validation error, or null if valid.</param>
public readonly record struct PinValidationResult(bool IsValid, PinValidationError? Error);

/// <summary>
/// PIN format validation errors.
/// </summary>
public enum PinValidationError
{
    /// <summary>PIN is shorter than the minimum length (4 digits).</summary>
    TooShort,

    /// <summary>PIN exceeds the maximum length (8 digits).</summary>
    TooLong,

    /// <summary>PIN contains non-digit characters.</summary>
    InvalidChars
}
