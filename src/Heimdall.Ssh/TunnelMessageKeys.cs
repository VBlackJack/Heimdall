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
/// Locale keys of the tunnel failure sentences this layer composes itself. The text
/// lives in the application's locale catalogues and is formatted there; the tunnel
/// manager and the plink runner only name the sentence and supply its arguments.
/// </summary>
public static class TunnelMessageKeys
{
    /// <summary>The requested local port is already tracked by an open tunnel; {0} is the port.</summary>
    public const string MessageKeyLocalPortInUse = "ErrorTunnelLocalPortInUse";

    /// <summary>The local port was claimed by a concurrent registration.</summary>
    public const string MessageKeyLocalPortClaimedConcurrently = "ErrorTunnelPortConcurrent";

    /// <summary>The tunnel manager was disposed before the tunnel could be registered.</summary>
    public const string MessageKeyManagerDisposed = "ErrorTunnelManagerDisposed";

    /// <summary>Establishment of a single-hop tunnel was cancelled.</summary>
    public const string MessageKeyEstablishmentCancelled = "ErrorTunnelEstablishmentCancelled";

    /// <summary>Establishment of a chained tunnel was cancelled.</summary>
    public const string MessageKeyChainedEstablishmentCancelled = "ErrorTunnelChainedEstablishmentCancelled";

    /// <summary>A chained open was requested with no gateway at all.</summary>
    public const string MessageKeyGatewayChainEmpty = "ErrorTunnelGatewayChainEmpty";

    /// <summary>The plink process could not be started; {0} is the underlying error.</summary>
    public const string MessageKeyPlinkProcessStartFailed = "ErrorPlinkProcessStartFailed";

    /// <summary>The configured plink executable does not exist; {0} is the path.</summary>
    public const string MessageKeyPlinkExecutableNotFound = "ErrorPlinkExecutableNotFound";
}
