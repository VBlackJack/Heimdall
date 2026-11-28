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

namespace Heimdall.Ssh;

/// <summary>
/// Port forwarding mode for a direct session-bound forward on an active <see cref="SshShellSession"/>.
/// </summary>
public enum SshSessionForwardKind
{
    /// <summary>Local port forward (<c>-L</c>): binds locally and tunnels to a remote host and port.</summary>
    Local,

    /// <summary>Dynamic SOCKS5 proxy (<c>-D</c>): binds a local SOCKS5 listener.</summary>
    DynamicSocks,

    /// <summary>Remote reverse forward (<c>-R</c>): binds on the remote SSH server and forwards to a local port.</summary>
    Remote
}

/// <summary>
/// Request describing a hot-added port forward on a live <see cref="SshShellSession"/>.
/// </summary>
public sealed record SshSessionForwardRequest(
    SshSessionForwardKind Kind,
    int LocalPort,
    string? RemoteHost = null,
    int RemotePort = 0,
    string? LocalBindHost = null,
    string? Label = null);

/// <summary>
/// Result of attempting to hot-add a port forward onto an active <see cref="SshShellSession"/>.
/// </summary>
public sealed record SshSessionForwardResult(
    bool Success,
    TunnelInfo? Forward = null,
    string? ErrorMessage = null);
