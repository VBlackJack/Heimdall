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

using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>Why a tool can or cannot be used right now.</summary>
public enum ToolAvailabilityState
{
    /// <summary>The tool may be listed and opened.</summary>
    Available,

    /// <summary>The user switched the tool's pack off in Settings.</summary>
    DisabledByUser,

    /// <summary>The organisation forbids the tool's pack; this beats the user's choice.</summary>
    ForbiddenByPolicy,

    /// <summary>The id is not in the tool registry.</summary>
    Unknown,
}

/// <summary>
/// The single decision on whether a tool may be used, combining the registry, the user's
/// settings and the organisation policy.
/// </summary>
public interface IToolAvailability
{
    /// <summary>
    /// Raised when the effective set of enabled packs differs after a settings change; an
    /// unrelated settings write does not raise it.
    /// </summary>
    /// <remarks>
    /// Raised on the thread that published the settings change, which is a thread-pool thread, not
    /// the UI thread. A subscriber that touches the UI must marshal itself.
    /// </remarks>
    event Action? Changed;

    /// <summary>The state of the tool with the given id (the <c>TOOL:</c> prefix is accepted).</summary>
    ToolAvailabilityState GetState(string toolId);

    /// <summary>True when the pack's tools may be used. <see cref="ToolPack.Core"/> is always enabled.</summary>
    bool IsPackEnabled(ToolPack pack);

    /// <summary>True when the organisation policy forbids the pack, whatever the user chose.</summary>
    bool IsPackForbiddenByPolicy(ToolPack pack);
}
