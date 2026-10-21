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

namespace Heimdall.App.Services;

/// <summary>
/// What the Command Library remembers for the length of the application session, across its
/// tabs: the category of the last action the operator created.
/// </summary>
/// <remarks>
/// A captured command opens the editor in that category, on the bet that commands captured
/// one after another during the same piece of work belong together. Registered as a singleton
/// because every library tab is a new view model; never persisted.
/// </remarks>
public sealed class CommandLibrarySessionMemory
{
    /// <summary>The category of the last action created in this session, or null before the first.</summary>
    public string? LastCreatedCategory { get; set; }
}
