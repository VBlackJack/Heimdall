// Copyright 2026 Julien Bombled
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Outcome of a tunnel setup attempt, carrying the structured failure code that the
/// previous five-value tuple could not transport.
/// </summary>
public sealed record TunnelSetupOutcome(
    bool Success,
    bool UsesTunnel,
    string Host,
    int Port,
    string? ErrorMessage,
    SshFailureCode? FailureCode)
{
    /// <summary>
    /// Whether an already-open tunnel was handed back rather than a new one opened.
    /// </summary>
    /// <remarks>
    /// Carried out of the tunnel layer because a reused tunnel's route is not the one this
    /// connection resolved. <see cref="TunnelResult.ReusedExistingTunnel"/> says why the two can
    /// disagree: the reuse key hashes gateway identifiers, which an edit leaves alone.
    /// </remarks>
    public bool ReusedExistingTunnel { get; init; }

    /// <summary>
    /// The gateway chain the tunnel this connection is on was opened through, as it read when
    /// that tunnel was dialled; null for a direct connection and for a tunnel this process did
    /// not open.
    /// </summary>
    /// <remarks>
    /// The route a certificate question may name, and the reason it can be named at all for a
    /// reused tunnel. See <see cref="TunnelResult.GatewayRoute"/> for why it is recorded at the
    /// dial rather than resolved by whoever asks.
    /// </remarks>
    public string? GatewayRoute { get; init; }

    /// <summary>
    /// Five-value deconstruction kept for source compatibility with the previous tuple
    /// contract, so existing consumers continue to compile unchanged.
    /// </summary>
    public void Deconstruct(
        out bool success,
        out bool usesTunnel,
        out string host,
        out int port,
        out string? errorMessage)
    {
        success = Success;
        usesTunnel = UsesTunnel;
        host = Host;
        port = Port;
        errorMessage = ErrorMessage;
    }
}
