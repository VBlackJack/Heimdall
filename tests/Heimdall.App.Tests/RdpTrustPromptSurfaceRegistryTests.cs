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

using Heimdall.App.Services;
using Heimdall.Core.Certificates;

namespace Heimdall.App.Tests;

/// <summary>
/// Which pane a certificate question is routed to, and what happens when that pane is gone.
/// </summary>
public sealed class RdpTrustPromptSurfaceRegistryTests
{
    [Fact]
    public void Find_ReturnsTheSurfaceRegisteredUnderThatScope()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        FakeSurface first = new();
        FakeSurface second = new();
        using IDisposable one = registry.Register("pane-1", first);
        using IDisposable two = registry.Register("pane-2", second);

        Assert.Same(first, registry.Find("pane-1"));
        Assert.Same(second, registry.Find("pane-2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pane-that-never-existed")]
    public void Find_WithNothingToResolve_ReturnsNothingRatherThanAnything(string? scopeId)
    {
        // The whole point of the indirection. A caller that gets null refuses; a caller that
        // got "some surface" would ask a question about one machine at another machine's pane,
        // which is the defect this replaced.
        RdpTrustPromptSurfaceRegistry registry = new();
        using IDisposable registration = registry.Register("pane-1", new FakeSurface());

        Assert.Null(registry.Find(scopeId));
    }

    [Fact]
    public void DisposingTheHandle_RemovesTheSurface()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        IDisposable registration = registry.Register("pane-1", new FakeSurface());
        Assert.Equal(1, registry.Count);

        registration.Dispose();

        Assert.Null(registry.Find("pane-1"));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DisposingTheHandleTwice_ChangesNothing()
    {
        RdpTrustPromptSurfaceRegistry registry = new();
        IDisposable registration = registry.Register("pane-1", new FakeSurface());

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void AStaleHandle_DoesNotUnregisterTheSurfaceThatTookItsScope()
    {
        // A teardown running late must not silence a live pane. With a plain Remove by key it
        // would, and the live pane's next question would be refused with no way to see why.
        RdpTrustPromptSurfaceRegistry registry = new();
        IDisposable stale = registry.Register("pane-1", new FakeSurface());
        FakeSurface live = new();
        using IDisposable current = registry.Register("pane-1", live);

        stale.Dispose();

        Assert.Same(live, registry.Find("pane-1"));
        Assert.Equal(1, registry.Count);
    }

    private sealed class FakeSurface : IRdpTrustPromptSurface
    {
        public Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
            => Task.FromResult(RdpTrustAnswer.Refuse);
    }
}
