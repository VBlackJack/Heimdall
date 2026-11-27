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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Represents a single importable OpenSSH host alias resolved from ssh_config.
/// </summary>
public sealed record OpenSshImportCandidate
{
    /// <summary>
    /// Gets the literal alias declared in the Host directive.
    /// </summary>
    public required string Alias { get; init; }

    /// <summary>
    /// Gets the resolved host name or address.
    /// </summary>
    public required string HostName { get; init; }

    /// <summary>
    /// Gets the SSH port. Defaults to 22.
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// Gets the optional SSH username.
    /// </summary>
    public string? User { get; init; }

    /// <summary>
    /// Gets the optional identity file path after tilde expansion.
    /// </summary>
    public string? IdentityFile { get; init; }

    /// <summary>
    /// Gets the 1-based source line number of the Host directive.
    /// </summary>
    public int SourceLineNumber { get; init; }

    /// <summary>
    /// Gets the resolved ProxyJump hops, ordered root-to-target.
    /// </summary>
    public IReadOnlyList<OpenSshProxyJumpHop> ProxyJumpChain { get; init; } = [];

    /// <summary>
    /// Gets whether <c>ForwardAgent yes</c> was configured for this host.
    /// </summary>
    public bool ForwardAgent { get; init; }

    /// <summary>
    /// Gets whether <c>Compression yes</c> was configured for this host.
    /// </summary>
    public bool Compression { get; init; }

    /// <summary>
    /// Gets the first valid <c>DynamicForward</c> port configured for this host, if any.
    /// </summary>
    public int? DynamicForwardPort { get; init; }

    /// <summary>
    /// Gets the remote bind port from the first valid <c>RemoteForward</c> directive, if any.
    /// </summary>
    public int? RemoteForwardBindPort { get; init; }

    /// <summary>
    /// Gets the local target port from the first valid <c>RemoteForward</c> directive, if any.
    /// </summary>
    public int? RemoteForwardLocalPort { get; init; }

    /// <summary>
    /// Gets raw <c>LocalForward</c> specifications captured for this host.
    /// </summary>
    public IReadOnlyList<string> LocalForwards { get; init; } = [];
}

/// <summary>
/// Represents one resolved OpenSSH ProxyJump hop.
/// </summary>
public sealed record OpenSshProxyJumpHop
{
    /// <summary>
    /// Gets the literal host token from the ProxyJump directive.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the resolved hostname after applying a matching Host block, when present.
    /// </summary>
    public required string HostName { get; init; }

    /// <summary>
    /// Gets the resolved SSH port.
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// Gets the resolved username.
    /// </summary>
    public string? User { get; init; }

    /// <summary>
    /// Gets the resolved identity file path for the hop, if configured.
    /// </summary>
    public string? IdentityFile { get; init; }

    /// <summary>
    /// Gets the 1-based source line number of the ProxyJump directive.
    /// </summary>
    public int SourceLineNumber { get; init; }
}
