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

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Seals and opens a value that must not sit in the database in clear text.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the persistence layer can depend on the behaviour rather than on the
/// application's concrete protector, which is a static holding process-wide vault state.
/// A test can then state what "locked" or "unreadable" means instead of mutating that
/// global state and racing every other test in the run.
/// </para>
/// <para>
/// The contract is deliberately asymmetric: sealing a value when the store cannot accept
/// one is an error the caller has to handle, while opening a value that cannot be read is
/// an ordinary outcome, because a stored value can outlive the key that sealed it.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Seals <paramref name="plainText"/> for storage.
    /// </summary>
    /// <exception cref="Exception">
    /// Thrown when the store cannot currently seal a value, for instance when a vault is
    /// configured but locked. Implementations must never silently fall back to a weaker
    /// form; the caller decides what to do without the write.
    /// </exception>
    string Protect(string plainText);

    /// <summary>
    /// Opens a value produced by <see cref="Protect"/>, or returns <see langword="null"/>
    /// when it cannot be read - a locked vault, a different machine, a different account.
    /// </summary>
    string? Unprotect(string protectedValue);
}
