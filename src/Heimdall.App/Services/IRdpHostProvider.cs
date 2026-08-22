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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Services;

/// <summary>
/// Where a session view gets its RDP ActiveX control, and where it gives it back.
/// </summary>
/// <remarks>
/// A view used to create its own control and dispose it, which cost a measured 66 kernel
/// handles per session that the operating system never returns. Going through a provider
/// lets those controls be reused without the view knowing whether it received a fresh one.
/// <b>UI thread only.</b>
/// </remarks>
public interface IRdpHostProvider
{
    /// <summary>Returns a control ready to be configured for a session.</summary>
    RdpActiveXHost Acquire();

    /// <summary>
    /// Hands a control back once its session is over. The provider decides whether it is
    /// kept or destroyed; the caller must not touch it again either way.
    /// </summary>
    void Release(RdpActiveXHost host);
}

/// <summary>
/// Creates a control per session and destroys it afterwards, which is what Heimdall did
/// before pooling existed.
/// </summary>
/// <remarks>
/// Kept as the default so a view constructed without a provider behaves exactly as it used
/// to, and so the pooling can be taken out of the picture when diagnosing.
/// </remarks>
public sealed class TransientRdpHostProvider : IRdpHostProvider
{
    public RdpActiveXHost Acquire() => new();

    public void Release(RdpActiveXHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Dispose();
    }
}

/// <summary>
/// Hands out controls from a small pool, so a session that follows another can inherit its
/// control instead of paying for a new one.
/// </summary>
public sealed class PooledRdpHostProvider : IRdpHostProvider, IDisposable
{
    private readonly ReusableHostPool<RdpActiveXHost> _pool;

    public PooledRdpHostProvider(int capacity = ReusableHostPool<RdpActiveXHost>.DefaultCapacity)
    {
        _pool = new ReusableHostPool<RdpActiveXHost>(
            static () => new RdpActiveXHost(),
            capacity,
            Core.Logging.FileLogger.Info);
    }

    /// <summary>Controls handed out that came from the pool. Exposed for diagnostics.</summary>
    public int ReuseCount => _pool.ReuseCount;

    /// <summary>Controls the pool had to build. Exposed for diagnostics.</summary>
    public int CreationCount => _pool.CreationCount;

    public RdpActiveXHost Acquire() => _pool.Acquire();

    public void Release(RdpActiveXHost host) => _pool.Release(host);

    public void Dispose() => _pool.Dispose();
}
