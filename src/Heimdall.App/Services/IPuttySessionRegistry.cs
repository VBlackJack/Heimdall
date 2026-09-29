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

using Microsoft.Win32;

namespace Heimdall.App.Services;

/// <summary>
/// One value of a PuTTY saved session, with the registry kind it is stored as.
/// </summary>
/// <param name="Name">The value name, for example <c>TermWidth</c>.</param>
/// <param name="Value">The raw value, never environment-expanded.</param>
/// <param name="Kind">The registry kind, preserved when the value is copied.</param>
public sealed record PuttyRegistryValue(string Name, object Value, RegistryValueKind Kind);

/// <summary>
/// The few operations Heimdall performs on PuTTY's saved sessions under HKCU.
/// </summary>
/// <remarks>
/// A seam on purpose. Writing temporary size sessions is a side effect on the user's own registry,
/// shared with PuTTY itself, so every write, copy and deletion has to be observable by a test
/// without touching the real hive.
/// </remarks>
public interface IPuttySessionRegistry
{
    /// <summary>Lists the saved session key names, as stored.</summary>
    IReadOnlyList<string> GetSessionNames();

    /// <summary>
    /// Reads every value of a saved session, or returns <see langword="null"/> when the session
    /// does not exist.
    /// </summary>
    IReadOnlyList<PuttyRegistryValue>? ReadSession(string sessionName);

    /// <summary>Creates the session key if needed and writes <paramref name="values"/> into it.</summary>
    void WriteSession(string sessionName, IReadOnlyList<PuttyRegistryValue> values);

    /// <summary>
    /// Deletes a Heimdall temporary session key. A missing key is not an error; a name that is not
    /// a Heimdall session is refused with <see cref="ArgumentException"/>.
    /// </summary>
    void DeleteSession(string sessionName);
}
