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
/// Argon2id cost parameters used to derive the vault key-encryption key (KEK)
/// from the user master password.
/// </summary>
/// <param name="MemoryKib">Memory cost in kibibytes (1 KiB = 1024 bytes).</param>
/// <param name="Iterations">Time cost: the number of passes over memory.</param>
/// <param name="Parallelism">Degree of parallelism (number of lanes).</param>
public readonly record struct Argon2idParameters(int MemoryKib, int Iterations, int Parallelism)
{
    /// <summary>
    /// Default memory cost in kibibytes (64 MiB). Exceeds the OWASP Password
    /// Storage minimum of 19 MiB for Argon2id while staying responsive on an
    /// interactive desktop unlock.
    /// </summary>
    private const int DefaultMemoryKib = 65536;

    /// <summary>
    /// Default time cost (passes). Three iterations is comfortably above the
    /// OWASP minimum of 2 at the chosen memory size.
    /// </summary>
    private const int DefaultIterations = 3;

    /// <summary>
    /// Default degree of parallelism. A single lane keeps derivation
    /// deterministic and portable; on a single-user desktop unlock additional
    /// lanes add little practical resistance.
    /// </summary>
    private const int DefaultParallelism = 1;

    /// <summary>
    /// Recommended interactive-unlock defaults (Argon2id, 64 MiB, t=3, p=1).
    /// Single named source for the cost parameters; callers must not hardcode
    /// these values elsewhere.
    /// </summary>
    public static Argon2idParameters Recommended { get; } =
        new(DefaultMemoryKib, DefaultIterations, DefaultParallelism);

    /// <summary>
    /// Whether all three cost parameters are within the bounds accepted by the
    /// Argon2 specification (memory and parallelism at least 1, iterations at
    /// least 1, and memory at least 8 * parallelism KiB).
    /// </summary>
    public bool IsValid =>
        Iterations >= 1 &&
        Parallelism >= 1 &&
        MemoryKib >= 8 * Parallelism;
}
