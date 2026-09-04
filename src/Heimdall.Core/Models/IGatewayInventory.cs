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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Models;

/// <summary>
/// The SSH gateways a tool may route through, as they stand now rather than as they stood when
/// the tool was opened.
/// </summary>
/// <remarks>
/// <para>A tool view used to receive the gateway list once, through
/// <see cref="ToolContext.SshGateways"/>, and dial whichever entry of that snapshot the user
/// picked for as long as the tab stayed open. A gateway edited in the settings meanwhile left
/// every open tool naming, and dialling, the old host, port and credentials. This is the live
/// counterpart: <see cref="Current"/> reads the inventory as it is, and <see cref="Changed"/>
/// announces every save.</para>
/// <para><see cref="Changed"/> is raised on the thread that saved the settings, which is not the
/// UI thread. A subscriber that touches a control marshals.</para>
/// </remarks>
public interface IGatewayInventory
{
    /// <summary>
    /// The gateways as the configuration holds them at the moment of the call.
    /// </summary>
    IReadOnlyList<SshGatewayDto> Current { get; }

    /// <summary>
    /// Raised after the settings are saved, with the gateways they now hold.
    /// </summary>
    event Action<IReadOnlyList<SshGatewayDto>>? Changed;
}
