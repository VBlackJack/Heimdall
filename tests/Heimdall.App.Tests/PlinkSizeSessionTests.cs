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

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.Ssh.Plink;
using Heimdall.Terminal;
using Microsoft.Win32;

namespace Heimdall.App.Tests;

/// <summary>
/// The temporary PuTTY saved session that carries the initial terminal size to a pipe-mode Plink.
/// </summary>
/// <remarks>
/// Windows Plink reads the PTY size only from <c>TermWidth</c>/<c>TermHeight</c> in its loaded
/// configuration, and <c>-load</c> replaces "Default Settings" entirely. These oracles pin what the
/// session contains, when it goes away, and that a registry refusal costs the size and nothing else.
/// </remarks>
public sealed class PlinkSizeSessionTests
{
    private const int Columns = 173;
    private const int Rows = 41;

    private const bool Attested = true;
    private const bool NotAttested = false;

    [Fact]
    public void TryCreate_CopiesDefaultSettingsWithTheirKinds_ThenSetsTheSize()
    {
        InMemoryPuttySessionRegistry registry = new();
        byte[] binary = [0x01, 0x02];
        registry.Seed(
            PlinkSizeSessionNaming.DefaultSettingsSessionName,
            new PuttyRegistryValue("ProxyHost", "proxy.example.test", RegistryValueKind.String),
            new PuttyRegistryValue("PortNumber", 2222, RegistryValueKind.DWord),
            new PuttyRegistryValue("Blob", binary, RegistryValueKind.Binary),
            new PuttyRegistryValue(PlinkSizeSession.TermWidthValueName, 80, RegistryValueKind.DWord),
            new PuttyRegistryValue(PlinkSizeSession.TermHeightValueName, 24, RegistryValueKind.DWord));

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, _ => { });

        name.Should().NotBeNull();
        IReadOnlyList<PuttyRegistryValue> written = registry.ReadSession(name!)!;
        written.Should().ContainEquivalentOf(new PuttyRegistryValue("ProxyHost", "proxy.example.test", RegistryValueKind.String));
        written.Should().ContainEquivalentOf(new PuttyRegistryValue("PortNumber", 2222, RegistryValueKind.DWord));
        written.Single(v => v.Name == "Blob").Kind.Should().Be(RegistryValueKind.Binary);
        written.Single(v => v.Name == "Blob").Value.Should().BeEquivalentTo(binary);

        // The size overrides the copied defaults, and appears exactly once each.
        written.Where(v => v.Name == PlinkSizeSession.TermWidthValueName).Should()
            .ContainSingle().Which.Should().Be(new PuttyRegistryValue(PlinkSizeSession.TermWidthValueName, Columns, RegistryValueKind.DWord));
        written.Where(v => v.Name == PlinkSizeSession.TermHeightValueName).Should()
            .ContainSingle().Which.Should().Be(new PuttyRegistryValue(PlinkSizeSession.TermHeightValueName, Rows, RegistryValueKind.DWord));
    }

    [Fact]
    public void TryCreate_NeverWritesHostName_EvenWhenDefaultSettingsCarriesOne()
    {
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed(
            PlinkSizeSessionNaming.DefaultSettingsSessionName,
            new PuttyRegistryValue("HostName", "default.example.test", RegistryValueKind.String),
            new PuttyRegistryValue("hostname", "lowercase.example.test", RegistryValueKind.String));

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, _ => { });

        registry.ReadSession(name!)!
            .Should().NotContain(v => string.Equals(v.Name, PlinkSizeSession.HostNameValueName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryCreate_WithoutDefaultSettings_WritesOnlyTheSize()
    {
        InMemoryPuttySessionRegistry registry = new();

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, _ => { });

        registry.ReadSession(name!)!.Select(v => v.Name).Should().BeEquivalentTo(
            PlinkSizeSession.TermWidthValueName,
            PlinkSizeSession.TermHeightValueName);
    }

    [Fact]
    public void TryCreate_NameCarriesThePrefixAndOnlyUnescapedCharacters()
    {
        InMemoryPuttySessionRegistry registry = new();

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, _ => { });

        name.Should().StartWith(PlinkSizeSessionNaming.Prefix);
        Regex.IsMatch(name!, "^[A-Za-z0-9-]+$").Should().BeTrue();
        PlinkSizeSessionNaming.IsHeimdallSession(name).Should().BeTrue();
    }

    [Fact]
    public void TryCreate_WriteFailure_WarnsReturnsNullAndRemovesThePartialKey()
    {
        InMemoryPuttySessionRegistry registry = new() { WriteFailure = new UnauthorizedAccessException("denied") };
        List<string> warnings = [];

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, warnings.Add);

        name.Should().BeNull();
        warnings.Should().ContainSingle().Which.Should().Contain("denied");
        registry.GetSessionNames().Should().NotContain(n => PlinkSizeSessionNaming.IsHeimdallSession(n));
        registry.Deleted.Should().ContainSingle();
    }

    [Fact]
    public void TryCreate_ReadFailure_WarnsAndReturnsNull()
    {
        InMemoryPuttySessionRegistry registry = new() { ReadFailure = new System.Security.SecurityException("no read") };
        List<string> warnings = [];

        string? name = PlinkSizeSession.TryCreate(registry, Columns, Rows, warnings.Add);

        name.Should().BeNull();
        warnings.Should().ContainSingle();
        registry.Written.Should().BeEmpty();
    }

    [Fact]
    public void Delete_Failure_WarnsInsteadOfThrowing()
    {
        InMemoryPuttySessionRegistry registry = new() { DeleteFailure = new IOException("busy") };
        List<string> warnings = [];

        Action delete = () => PlinkSizeSession.Delete(registry, PlinkSizeSessionNaming.CreateName(), warnings.Add);

        delete.Should().NotThrow();
        warnings.Should().ContainSingle().Which.Should().Contain("busy");
    }

    [Fact]
    public void Release_Attested_BeforeAnySignal_DeletesNothing()
    {
        FakeTerminalSession session = new();
        List<string> deleted = [];

        PlinkSizeSession.ArmRelease(session, "HeimdallPtySize-a", deleted.Add, Attested);

        deleted.Should().BeEmpty();
        session.DataSubscribers.Should().Be(1);
        session.ExitSubscribers.Should().Be(1);
    }

    [Fact]
    public void Release_Attested_FirstByte_DeletesOnceAndStopsListening()
    {
        FakeTerminalSession session = new();
        List<string> deleted = [];
        PlinkSizeSession.ArmRelease(session, "HeimdallPtySize-a", deleted.Add, Attested);

        session.RaiseData([0x24]);
        session.RaiseData([0x0A]);
        session.RaiseExit(0);

        deleted.Should().Equal("HeimdallPtySize-a");
        session.DataSubscribers.Should().Be(0);
        session.ExitSubscribers.Should().Be(0);
    }

    [Fact]
    public void Release_Unattested_OutputDeletesNothing_ExitDeletesOnce()
    {
        FakeTerminalSession session = new();
        List<string> deleted = [];
        PlinkSizeSession.ArmRelease(session, "HeimdallPtySize-a", deleted.Add, NotAttested);

        session.DataSubscribers.Should().Be(0);
        session.RaiseData([0x24]);
        deleted.Should().BeEmpty();

        session.RaiseExit(1);
        session.RaiseExit(1);

        deleted.Should().Equal("HeimdallPtySize-a");
    }

    [Fact]
    public void Release_ExplicitThenExit_DeletesOnce()
    {
        // The launch failure and cancellation paths dispose the session, which can raise
        // ProcessExited, and then release explicitly: both arrive at the same handle.
        FakeTerminalSession session = new();
        List<string> deleted = [];
        PlinkSizeSessionReleaseHandle handle =
            PlinkSizeSession.ArmRelease(session, "HeimdallPtySize-a", deleted.Add, Attested);

        session.RaiseExit(-1);
        handle.Release();
        handle.Release();

        deleted.Should().Equal("HeimdallPtySize-a");
    }

    [Fact]
    public void Janitor_DeletesOnlyPrefixedSessions()
    {
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed(PlinkSizeSessionNaming.DefaultSettingsSessionName);
        registry.Seed("prod-bastion");
        registry.Seed("heimdallptysize-lookalike");
        registry.Seed("HeimdallPtySize");
        registry.Seed("xHeimdallPtySize-embedded");
        registry.Seed($"{PlinkSizeSessionNaming.Prefix}crash1");
        registry.Seed($"{PlinkSizeSessionNaming.Prefix}crash2");

        int removed = new PlinkSizeSessionJanitor(registry, _ => { }).SweepLeftovers();

        removed.Should().Be(2);
        registry.Deleted.Should().BeEquivalentTo(
            $"{PlinkSizeSessionNaming.Prefix}crash1",
            $"{PlinkSizeSessionNaming.Prefix}crash2");
        registry.GetSessionNames().Should().BeEquivalentTo(
            PlinkSizeSessionNaming.DefaultSettingsSessionName,
            "prod-bastion",
            "heimdallptysize-lookalike",
            "HeimdallPtySize",
            "xHeimdallPtySize-embedded");
    }

    /// <summary>
    /// A size session this process created survives the sweep.
    /// </summary>
    /// <remarks>
    /// Audit 2026-09-30 S-04. A portable and an installed Heimdall share HKCU's PuTTY sessions,
    /// and the janitor swept every prefixed key when an SSH handler was built, including the key
    /// another running instance had just created for a Plink that had not read it yet. The same
    /// sweep run by a second handler in one process did the same to the first handler's key. The
    /// key names its owner now, and only a key whose owner has gone is removed.
    /// </remarks>
    [Fact]
    public void Janitor_KeepsASessionWhoseOwnerIsStillRunning()
    {
        InMemoryPuttySessionRegistry registry = new();
        string? live = PlinkSizeSession.TryCreate(registry, Columns, Rows, _ => { });
        live.Should().NotBeNull();

        int removed = new PlinkSizeSessionJanitor(registry, _ => { }).SweepLeftovers();

        removed.Should().Be(0);
        registry.GetSessionNames().Should().Contain(live);
    }

    /// <summary>
    /// The same owner check, with the owner written out as another running instance would.
    /// </summary>
    [Fact]
    public void Janitor_KeepsASessionNamedAfterARunningProcess()
    {
        using Process current = Process.GetCurrentProcess();
        string name = OwnedName(current.Id, current.StartTime.ToUniversalTime().Ticks);
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed(name);

        new PlinkSizeSessionJanitor(registry, _ => { }).SweepLeftovers();

        registry.GetSessionNames().Should().Contain(name);
    }

    /// <summary>
    /// A reused process id is not a live owner: the start time must match too.
    /// </summary>
    [Fact]
    public void Janitor_RemovesASessionWhoseProcessIdWasReused()
    {
        using Process current = Process.GetCurrentProcess();
        long earlierStart = current.StartTime.ToUniversalTime().AddDays(-1).Ticks;
        string name = OwnedName(current.Id, earlierStart);
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed(name);

        int removed = new PlinkSizeSessionJanitor(registry, _ => { }).SweepLeftovers();

        removed.Should().Be(1);
        registry.GetSessionNames().Should().NotContain(name);
    }

    /// <summary>
    /// A name in the format of the previous release carries no owner and is swept as before.
    /// </summary>
    [Fact]
    public void Janitor_RemovesALegacySessionWithoutAnOwner()
    {
        string legacy = $"{PlinkSizeSessionNaming.Prefix}{Guid.NewGuid():N}";
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed(legacy);

        int removed = new PlinkSizeSessionJanitor(registry, _ => { }).SweepLeftovers();

        removed.Should().Be(1);
    }

    [Theory]
    [InlineData("HeimdallPtySize-p12-t0-00000000000000000000000000000000x")]
    [InlineData("HeimdallPtySize-p-12-t1f-00000000000000000000000000000000")]
    [InlineData("HeimdallPtySize-p12-tZZ-00000000000000000000000000000000")]
    [InlineData("HeimdallPtySize-p99999999999-t1f-00000000000000000000000000000000")]
    [InlineData("heimdallptysize-p12-t1f-00000000000000000000000000000000")]
    public void OwnerParsing_RefusesAnythingButTheExactFormat(string name)
    {
        PlinkSizeSessionNaming.TryParseOwner(name, out _).Should().BeFalse();
    }

    [Fact]
    public void CreatedNames_StayInTheUnescapedAlphabet_AndNameTheirOwner()
    {
        string name = PlinkSizeSessionNaming.CreateName();

        Regex.IsMatch(name, "^[A-Za-z0-9-]+$").Should().BeTrue();
        PlinkSizeSessionNaming.TryParseOwner(name, out PlinkSizeSessionOwner owner).Should().BeTrue();
        owner.ProcessId.Should().Be(Environment.ProcessId);
    }

    private static string OwnedName(int processId, long startTicks) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{PlinkSizeSessionNaming.Prefix}p{processId}-t{startTicks:x}-{Guid.NewGuid():N}");

    [Fact]
    public void Janitor_EnumerationFailure_WarnsAndRemovesNothing()
    {
        InMemoryPuttySessionRegistry registry = new() { EnumerateFailure = new IOException("hive") };
        List<string> warnings = [];

        int removed = new PlinkSizeSessionJanitor(registry, warnings.Add).SweepLeftovers();

        removed.Should().Be(0);
        warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task PuttyImport_SkipsHeimdallSizeSessions()
    {
        InMemoryPuttySessionRegistry registry = new();
        registry.Seed("prod-bastion", new PuttyRegistryValue("HostName", "bastion.example.test", RegistryValueKind.String));
        registry.Seed($"{PlinkSizeSessionNaming.Prefix}leftover", new PuttyRegistryValue("TermWidth", Columns, RegistryValueKind.DWord));

        IReadOnlyList<Heimdall.Core.Ssh.RawPuttySession> sessions =
            await new WindowsPuttyRegistrySource(registry).ReadSessionsAsync();

        sessions.Select(s => s.EncodedSessionName).Should().Equal("prod-bastion");
        sessions[0].Values["HostName"].Should().Be("bastion.example.test");
    }

    /// <summary>
    /// Counts live subscribers so the oracles can see the unsubscription.
    /// </summary>
    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly object _sync = new();
        private Action<ReadOnlyMemory<byte>>? _data;
        private Action<int>? _exit;

        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { lock (_sync) { _data += value; } }
            remove { lock (_sync) { _data -= value; } }
        }

        public event Action<int>? ProcessExited
        {
            add { lock (_sync) { _exit += value; } }
            remove { lock (_sync) { _exit -= value; } }
        }

        public int DataSubscribers
        {
            get { lock (_sync) { return _data?.GetInvocationList().Length ?? 0; } }
        }

        public int ExitSubscribers
        {
            get { lock (_sync) { return _exit?.GetInvocationList().Length ?? 0; } }
        }

        public bool IsRunning => true;

        public int? ProcessId => 4242;

        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        public void RaiseData(byte[] chunk)
        {
            Action<ReadOnlyMemory<byte>>? handler;
            lock (_sync)
            {
                handler = _data;
            }

            handler?.Invoke(chunk.AsMemory());
        }

        public void RaiseExit(int exitCode)
        {
            Action<int>? handler;
            lock (_sync)
            {
                handler = _exit;
            }

            handler?.Invoke(exitCode);
        }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }
}
