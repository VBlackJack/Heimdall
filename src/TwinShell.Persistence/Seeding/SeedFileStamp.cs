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

namespace TwinShell.Persistence.Seeding;

/// <summary>
/// What the file system says about one seed file without reading it: its size and the moment it
/// was last written.
/// </summary>
/// <param name="Length">Size in bytes.</param>
/// <param name="LastWriteUtcTicks">Last write time, as UTC ticks.</param>
public readonly record struct SeedFileStamp(long Length, long LastWriteUtcTicks)
{
    /// <summary>Reads the stamp of <paramref name="path"/> from the file system.</summary>
    public static SeedFileStamp FromFileSystem(string path)
    {
        var info = new FileInfo(path);
        return new SeedFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
