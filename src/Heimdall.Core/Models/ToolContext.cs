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
/// Carries optional context data when opening a tool tab.
/// Enriched fields allow tools to prefill with server-specific values
/// instead of generic placeholders like "example.com".
/// <see cref="DocumentContent"/> hands the tool an unsaved document body (never a
/// path): the receiving tool must ask for a location before its first write.
/// </summary>
public sealed record ToolContext(
    string? TargetHost = null,
    int? TargetPort = null,
    string? Argument = null,
    string? DisplayName = null,
    string? Username = null,
    string? ConnectionType = null,
    string? ProjectName = null,
    string? GroupName = null,
    string? SourceServerId = null,
    System.Collections.IList? SshGateways = null,
    Delegate? OpenToolAction = null,
    Delegate? OpenSessionAction = null,
    Delegate? AddServerAction = null,
    Action<bool>? SetBusyAction = null,
    Action<string>? SendCommandAction = null,
    Func<bool>? CanSendToTerminal = null,
    ICommandBroadcaster? CommandBroadcaster = null,
    string? InitialActionId = null,
    IGatewayInventory? GatewayInventory = null,
    string? DocumentContent = null);
