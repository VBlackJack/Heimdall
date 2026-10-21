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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Builds the <see cref="ToolContext"/> a tool entry of the inventory opens with, on each path
/// that opens one: the tree and its context menu, and the connect pipeline.
/// </summary>
/// <remarks>
/// Every path ends in <see cref="EmbeddedSessionManager.WithSessionProfile"/>, the helper the
/// session manager uses for a tool docked beside a terminal, so the facts a profile declares -
/// its far-end operating system first of all - reach the tool the same way whichever path
/// opened it. Two of three paths once built the context by hand and dropped the declaration.
/// </remarks>
internal static class ToolEntryContextFactory
{
    /// <summary>The context for a tool entry opened from the tree or its context menu.</summary>
    public static ToolContext ForTreeEntry(ServerItemViewModel tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var context = new ToolContext(
            TargetHost: tool.RemoteServer,
            TargetPort: tool.RemotePort > 0 ? tool.RemotePort : null,
            Argument: tool.RemoteServer);
        return EmbeddedSessionManager.WithSessionProfile(context, tool.SourceProfile)!;
    }

    /// <summary>The context for a tool entry opened through the connect pipeline.</summary>
    public static ToolContext ForConnectedEntry(ServerProfileDto profile, string? projectName)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var context = new ToolContext(
            TargetHost: profile.RemoteServer,
            TargetPort: profile.RemotePort > 0 ? profile.RemotePort : null,
            Argument: profile.RemoteServer,
            DisplayName: profile.DisplayName,
            Username: profile.SshUsername ?? profile.RdpUsername,
            ConnectionType: profile.ConnectionType,
            ProjectName: projectName);
        return EmbeddedSessionManager.WithSessionProfile(context, profile)!;
    }
}
