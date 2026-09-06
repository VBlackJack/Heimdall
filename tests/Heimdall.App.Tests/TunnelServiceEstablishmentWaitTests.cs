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

using System.IO;
using Heimdall.App.Services;
using Heimdall.Ssh;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

/// <summary>
/// What happens to a tunnel whose establishment wait is cancelled. The tunnel
/// is registered, with one reference, before the wait; the connection state
/// learns its port only after it. A cancellation in between used to leave a
/// tunnel nobody knew the port of: the orphan cleanup on close found nothing
/// to release, and the SSH.NET or plink tunnel stayed open until the
/// application exited.
/// </summary>
public sealed class TunnelServiceEstablishmentWaitTests
{
    private const int LocalPort = 13389;
    private const int EstablishmentDelayMs = 2500;
    private const string ServiceSourcePath = "Services/TunnelService.cs";
    private const string ReleasingWaitCall = "await WaitForTunnelEstablishmentOrReleaseAsync(";
    private const string BareWaitCall = "await WaitForTunnelEstablishmentAsync(";

    /// <summary>
    /// Both opening paths, the SSH.NET one and the Plink fallback, register
    /// their tunnel and then wait. Each must wait through the releasing
    /// helper; the bare delay is awaited only inside that helper.
    /// </summary>
    [Fact]
    public void BothOpeningPaths_WaitThroughTheReleasingHelper()
    {
        string source = ReadAppSource(ServiceSourcePath);

        Assert.Equal(2, CountOccurrences(source, ReleasingWaitCall));
        Assert.Equal(1, CountOccurrences(source, BareWaitCall));
    }

    [Fact]
    public async Task WaitCancelledDuringTheDelay_ReleasesTheRegisteredTunnel()
    {
        using TunnelManager manager = new TunnelManager();
        FakeHandle handle = new FakeHandle();
        Assert.True(manager.TryRegisterExternalTunnel(RegisteredTunnel(), handle, static () => true));
        FakeTimeProvider clock = new FakeTimeProvider();
        using CancellationTokenSource cancellation = new CancellationTokenSource();

        Task wait = TunnelService.WaitForTunnelEstablishmentOrReleaseAsync(
            manager,
            LocalPort,
            EstablishmentDelayMs,
            clock,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(manager.HasTunnel(LocalPort));
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task WaitThatRunsToCompletion_KeepsTheTunnel()
    {
        using TunnelManager manager = new TunnelManager();
        FakeHandle handle = new FakeHandle();
        Assert.True(manager.TryRegisterExternalTunnel(RegisteredTunnel(), handle, static () => true));
        FakeTimeProvider clock = new FakeTimeProvider();

        Task wait = TunnelService.WaitForTunnelEstablishmentOrReleaseAsync(
            manager,
            LocalPort,
            EstablishmentDelayMs,
            clock,
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(EstablishmentDelayMs));

        await wait;
        Assert.True(manager.HasTunnel(LocalPort));
        Assert.False(handle.Disposed);
    }

    private static TunnelInfo RegisteredTunnel() =>
        new TunnelInfo(
            ServerName: "gw.example.test",
            LocalPort: LocalPort,
            RemoteHost: "target.example.test",
            RemotePort: 3389,
            StartedAt: DateTime.UtcNow,
            IsAlive: true);

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }

    private sealed class FakeHandle : IDisposable
    {
        private int _disposeCount;

        public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }
}
