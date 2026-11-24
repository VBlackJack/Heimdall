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

namespace Heimdall.Core.Models;

/// <summary>
/// The group a built-in tool belongs to, the unit an organisation or a user switches on or off.
/// </summary>
/// <remarks>
/// The member names are persisted: <c>AppSettings.DisabledToolPacks</c> stores them as text and
/// the machine policy names one registry value after each optional pack, so renaming a member
/// is a breaking change to both.
/// </remarks>
public enum ToolPack
{
    /// <summary>The permanent set. It can never be disabled, by the user or by policy.</summary>
    Core,

    /// <summary>Pure computation and formatting tools that touch no network and no system state.</summary>
    Essentials,

    /// <summary>Ordinary network diagnostics: probes, lookups and local network state.</summary>
    NetworkDiagnostics,

    /// <summary>Tools that read or edit the local system: logs, hosts file, services, diagrams.</summary>
    Administration,

    /// <summary>Scanning, enumeration and credential-testing tools, sensitive by nature.</summary>
    Audit,

    /// <summary>
    /// Tools described by recipe files written by other people, each one running a program already
    /// present on this machine. Off by default, on fresh and on existing installs.
    /// </summary>
    Recipes,
}
