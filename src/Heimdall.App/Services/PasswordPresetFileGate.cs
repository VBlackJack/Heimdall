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

using System.Collections.Concurrent;
using System.IO;

namespace Heimdall.App.Services;

/// <summary>
/// One lock per preset file, shared by the storage that saves it and the vault store that rewrites
/// it, so that a save landing between the store's read and its write is not lost.
/// </summary>
internal static class PasswordPresetFileGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The lock of <paramref name="filePath"/>.</summary>
    public static SemaphoreSlim For(string filePath) =>
        Gates.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));
}
