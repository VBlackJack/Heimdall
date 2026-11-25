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

using Heimdall.App.Services;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// An <see cref="IToolAvailability"/> that allows every tool, for the tests that build a view
/// model or a service only because its constructor asks for one.
/// </summary>
/// <remarks>
/// It never reaches a registry or a settings file, so it works without an application. A test that
/// is about availability builds a real <see cref="ToolAvailability"/> instead.
/// </remarks>
internal sealed class AlwaysAvailableToolAvailability : IToolAvailability
{
    /// <summary>The shared instance; the type holds no state.</summary>
    public static readonly AlwaysAvailableToolAvailability Instance = new();

    private AlwaysAvailableToolAvailability()
    {
    }

    /// <inheritdoc />
    public event Action? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public ToolAvailabilityState GetState(string toolId) => ToolAvailabilityState.Available;

    /// <inheritdoc />
    public bool IsPackEnabled(ToolPack pack) => true;

    /// <inheritdoc />
    public bool IsPackForbiddenByPolicy(ToolPack pack) => false;
}
