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

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// The one tunnel reference a connect attempt took, given back exactly once unless ownership
/// is handed to the session that outlives the attempt.
/// </summary>
/// <remarks>
/// Held in a <c>using</c> from the moment the tunnel is up, so any exception between the setup
/// and the hand-over gives the reference back. <see cref="Transfer"/> is called only on the
/// success path: after it, <see cref="Dispose"/> does nothing and whoever now owns the session
/// releases the reference (see <see cref="Release"/>).
/// </remarks>
internal sealed class TunnelLease : IDisposable
{
    private readonly ITunnelService _tunnelService;
    private readonly bool _usesTunnel;
    private readonly int _localPort;
    private readonly long _leaseId;
    private int _released;
    private int _transferred;

    public TunnelLease(ITunnelService tunnelService, bool usesTunnel, int localPort, long leaseId)
    {
        ArgumentNullException.ThrowIfNull(tunnelService);
        _tunnelService = tunnelService;
        _usesTunnel = usesTunnel;
        _localPort = localPort;
        _leaseId = leaseId;
    }

    /// <summary>
    /// Hands ownership away: a later <see cref="Dispose"/> no longer releases the reference.
    /// </summary>
    public void Transfer() => Interlocked.Exchange(ref _transferred, 1);

    /// <summary>
    /// Gives the reference back, once. Safe to call from any thread and any number of times.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (!_usesTunnel || _localPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(_localPort, _leaseId);
    }

    /// <summary>Releases the reference unless ownership was transferred.</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _transferred) != 0)
        {
            return;
        }

        Release();
    }
}
