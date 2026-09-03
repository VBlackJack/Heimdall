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

using Heimdall.Core.Certificates;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>A session surface that can put a certificate question to its own user.</summary>
/// <remarks>
/// One implementation, the embedded RDP view. The interface exists so the routing can be
/// driven in a test without a <c>UserControl</c>, and so nothing in the trust path holds a
/// reference to a WPF type.
/// </remarks>
internal interface IRdpTrustPromptSurface
{
    /// <summary>Puts the question inside this surface and waits for the answer.</summary>
    /// <param name="context">What the user needs in order to answer.</param>
    /// <param name="cancellationToken">Withdraws the question.</param>
    /// <remarks>
    /// Blocks the caller, which is the connection this surface is opening, and nothing else.
    /// Every way of not answering - the surface torn down, the token cancelled, the question
    /// withdrawn because another pane answered it - resolves to
    /// <see cref="RdpTrustAnswer.NotAsked"/>, which stops the connection without reporting an
    /// answer the user never gave.
    /// </remarks>
    Task<RdpTrustAnswer> AskAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Maps the opaque scope token a verification request carries to the surface that minted it.
/// </summary>
/// <remarks>
/// <para>The indirection exists because the request travels through <c>Heimdall.Core</c>, which
/// must not know what a pane is. The token is minted by the surface, carried through the core
/// untouched, and resolved back here.</para>
/// <para><b>An unknown token resolves to nothing, and nothing is not a default surface.</b> The
/// caller refuses rather than falling back to a window of its own choosing: falling back is
/// exactly the defect being removed, a question about one machine answered at another machine's
/// window.</para>
/// </remarks>
internal sealed class RdpTrustPromptSurfaceRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IRdpTrustPromptSurface> _surfaces =
        new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="surface"/> under <paramref name="scopeId"/>.</summary>
    /// <returns>A handle that removes the registration, and only this registration.</returns>
    /// <remarks>
    /// The handle removes the entry only while it is still the one that was registered, so a
    /// late teardown cannot unregister a surface that has since taken the same token. Disposing
    /// it twice is a no-op.
    /// </remarks>
    public IDisposable Register(string scopeId, IRdpTrustPromptSurface surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(surface);

        lock (_sync)
        {
            _surfaces[scopeId] = surface;
        }

        return new Registration(this, scopeId, surface);
    }

    /// <summary>The surface registered under <paramref name="scopeId"/>, or null.</summary>
    public IRdpTrustPromptSurface? Find(string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return null;
        }

        lock (_sync)
        {
            return _surfaces.TryGetValue(scopeId, out IRdpTrustPromptSurface? surface)
                ? surface
                : null;
        }
    }

    /// <summary>How many surfaces are currently registered.</summary>
    /// <remarks>
    /// Exposed so a test can prove a teardown removed its own entry rather than merely
    /// stopping it from answering.
    /// </remarks>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _surfaces.Count;
            }
        }
    }

    private void Unregister(string scopeId, IRdpTrustPromptSurface surface)
    {
        lock (_sync)
        {
            if (_surfaces.TryGetValue(scopeId, out IRdpTrustPromptSurface? current)
                && ReferenceEquals(current, surface))
            {
                _ = _surfaces.Remove(scopeId);
            }
        }
    }

    private sealed class Registration(
        RdpTrustPromptSurfaceRegistry owner,
        string scopeId,
        IRdpTrustPromptSurface surface) : IDisposable
    {
        private RdpTrustPromptSurfaceRegistry? _owner = owner;

        public void Dispose()
        {
            RdpTrustPromptSurfaceRegistry? target = Interlocked.Exchange(ref _owner, null);
            if (target is null)
            {
                return;
            }

            target.Unregister(scopeId, surface);
            FileLogger.Info($"[RdpCertPrompt] surface {scopeId} unregistered.");
        }
    }
}
