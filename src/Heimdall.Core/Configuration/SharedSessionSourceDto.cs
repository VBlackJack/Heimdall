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

namespace Heimdall.Core.Configuration;

/// <summary>
/// A MobaXterm session file Heimdall reads, and never writes, as a read-only source of sessions.
/// </summary>
public sealed class SharedSessionSourceDto
{
    /// <summary>
    /// Stable identifier, part of every identifier minted for this source's profiles and gateways
    /// (see <see cref="SharedSessionIds"/>). Changing it orphans the post-connect approvals.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The name the user gave the source.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Path of the .mxtsessions file; environment variables such as <c>%OneDriveCommercial%</c>
    /// are expanded when the file is read.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether the source is read at startup.</summary>
    public bool Enabled { get; set; } = true;
}
