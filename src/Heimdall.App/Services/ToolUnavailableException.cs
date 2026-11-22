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
/// Thrown when a tool control is requested for a tool whose pack is switched off or forbidden.
/// </summary>
/// <remarks>
/// A backstop: the entry points refuse such a request before it gets this far, so this is not
/// expected to fire in normal use. It is raised before any view is built.
/// </remarks>
public sealed class ToolUnavailableException : InvalidOperationException
{
    /// <summary>The locale key of the message for a tool whose pack the user switched off.</summary>
    public const string DisabledByUserMessageKey = "ErrorToolDisabledByUser";

    /// <summary>The locale key of the message for a tool whose pack the organisation forbids.</summary>
    public const string ForbiddenByPolicyMessageKey = "ErrorToolForbiddenByPolicy";

    public ToolUnavailableException(string toolId, ToolAvailabilityState state)
        : base($"Tool '{toolId}' is not available: {state}.")
    {
        ToolId = toolId;
        State = state;
    }

    /// <summary>The id of the refused tool, as it was requested.</summary>
    public string ToolId { get; }

    /// <summary>Why the tool is not available.</summary>
    public ToolAvailabilityState State { get; }

    /// <summary>
    /// True for the states that refuse a tool; <see cref="ToolAvailabilityState.Unknown"/> is not
    /// one of them, because an unknown id keeps showing its placeholder.
    /// </summary>
    public static bool IsRefusal(ToolAvailabilityState state) =>
        state is ToolAvailabilityState.DisabledByUser or ToolAvailabilityState.ForbiddenByPolicy;

    /// <summary>The locale key of the message that names the reason for a refusal.</summary>
    public static string MessageKeyFor(ToolAvailabilityState state) => state switch
    {
        ToolAvailabilityState.DisabledByUser => DisabledByUserMessageKey,
        ToolAvailabilityState.ForbiddenByPolicy => ForbiddenByPolicyMessageKey,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Only a refusal has a message."),
    };
}
