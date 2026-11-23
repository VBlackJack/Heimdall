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

/// <summary>
/// The one projection of the tool catalogue that every listing surface shows: the tools whose
/// <see cref="ToolAvailabilityState"/> is <see cref="ToolAvailabilityState.Available"/>.
/// </summary>
/// <remarks>
/// <see cref="ToolRegistry"/> keeps the full catalogue so a saved entry of a hidden tool still
/// resolves its name and icon. Detected external tools carry no pack and stay listed.
/// </remarks>
public static class ToolAvailabilityExtensions
{
    /// <summary>True when the tool may be shown in a list and opened.</summary>
    public static bool IsListed(this IToolAvailability availability, ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(descriptor);
        return availability.GetState(descriptor.Id) == ToolAvailabilityState.Available;
    }

    /// <summary>True when the tool with this id may be shown in a list and opened.</summary>
    public static bool IsListed(this IToolAvailability availability, string toolId)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return availability.GetState(toolId) == ToolAvailabilityState.Available;
    }

    /// <summary>The descriptors of <paramref name="tools"/> that may be shown, in their order.</summary>
    public static IEnumerable<ToolDescriptor> ListedTools(
        this IToolAvailability availability,
        IEnumerable<ToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Where(tool => availability.IsListed(tool));
    }
}
