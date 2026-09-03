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
    /// Carried out of the tunnel layer so a caller can decline to describe this connection's
    /// route. <see cref="TunnelResult.ReusedExistingTunnel"/> says why the two can disagree: the
    /// reuse key hashes gateway identifiers, which an edit leaves alone.
    /// </remarks>
    public bool ReusedExistingTunnel { get; init; }

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
