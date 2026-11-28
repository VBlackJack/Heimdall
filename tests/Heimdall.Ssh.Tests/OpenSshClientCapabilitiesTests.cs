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

namespace Heimdall.Ssh.Tests;

public sealed class OpenSshClientCapabilitiesTests
{
    private const string WindowsRoot = @"C:\Fake";
    private static readonly string Ssh = Path.Combine(WindowsRoot, "ssh.exe");

    private sealed class FakeRunner : IOpenSshProcessRunner
    {
        private readonly Func<string, IReadOnlyList<string>, TimeSpan, OpenSshProcessResult?> _handler;

        public FakeRunner(Func<string, IReadOnlyList<string>, TimeSpan, OpenSshProcessResult?> handler)
        {
            _handler = handler;
        }

        public int Calls { get; private set; }

        public OpenSshProcessResult? Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls++;
            return _handler(executablePath, arguments, timeout);
        }
    }

    private static FakeRunner RunnerFor(string banner, Func<string, OpenSshProcessResult?>? query = null)
    {
        return new FakeRunner((_, args, _) =>
        {
            if (args[0] == "-V")
            {
                return new OpenSshProcessResult(0, string.Empty, banner);
            }

            return query is null
                ? new OpenSshProcessResult(0, "alpha\r\nbeta\r\ngamma\r\n", string.Empty)
                : query(args[1]);
        });
    }

    private static OpenSshCapabilitiesReader ReaderFor(IOpenSshProcessRunner runner, Func<string, DateTime>? stamp = null)
    {
        return new OpenSshCapabilitiesReader(runner, stamp ?? (_ => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("OpenSSH_for_Windows_7.7p1, LibreSSL 2.6.5", 7, 7, 1)]
    [InlineData("OpenSSH_for_Windows_8.1p1, LibreSSL 3.0.2", 8, 1, 1)]
    [InlineData("OpenSSH_for_Windows_8.6p1, LibreSSL 3.4.3", 8, 6, 1)]
    [InlineData("OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2", 9, 5, 1)]
    public void ParseBanner_ReadsTheWindowsVersion(string banner, int major, int minor, int patch)
    {
        (OpenSshVersion? version, OpenSshRefusalReason? refusal) = OpenSshCapabilitiesReader.ParseBanner(banner);

        Assert.Null(refusal);
        Assert.Equal(new OpenSshVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("OpenSSH_for_Windows_x.y")]
    public void ParseBanner_RejectsGarbageAsUnreadable(string banner)
    {
        (OpenSshVersion? version, OpenSshRefusalReason? refusal) = OpenSshCapabilitiesReader.ParseBanner(banner);

        Assert.Null(version);
        Assert.Equal(OpenSshRefusalReason.VersionUnreadable, refusal);
    }

    [Fact]
    public void Read_RefusesAnMsysBuild()
    {
        var reader = ReaderFor(RunnerFor("OpenSSH_9.9p2, OpenSSL 3.2.2 4 Jun 2024"));

        OpenSshResolution result = reader.Read(Ssh);

        Assert.False(result.IsAccepted);
        Assert.Equal(OpenSshRefusalReason.NotWindowsBuild, result.Refusal);
    }

    [Fact]
    public void Read_RefusesWhenTheVersionCallFails()
    {
        var reader = ReaderFor(new FakeRunner((_, _, _) => null));

        Assert.Equal(OpenSshRefusalReason.VersionUnreadable, reader.Read(Ssh).Refusal);
    }

    [Fact]
    public void Read_ReturnsVersionAndAlgorithmLists()
    {
        var reader = ReaderFor(RunnerFor("OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2"));

        OpenSshClientCapabilities caps = reader.Read(Ssh).Capabilities!;

        Assert.Equal(new OpenSshVersion(9, 5, 1), caps.Version);
        Assert.True(caps.Kex.IsKnown);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, caps.Ciphers.Names);
    }

    [Fact]
    public void Read_CachesPerTimestamp()
    {
        DateTime stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FakeRunner runner = RunnerFor("OpenSSH_for_Windows_9.5p1");
        var reader = ReaderFor(runner, _ => stamp);

        reader.Read(Ssh);
        int afterFirst = runner.Calls;
        reader.Read(Ssh);
        Assert.Equal(afterFirst, runner.Calls);

        stamp = stamp.AddMinutes(1);
        reader.Read(Ssh);
        Assert.Equal(afterFirst * 2, runner.Calls);
    }

    [Fact]
    public void Read_AFailedQueryLeavesThatListUnknown()
    {
        FakeRunner runner = RunnerFor(
            "OpenSSH_for_Windows_9.5p1",
            kind => kind == "mac" ? null : new OpenSshProcessResult(0, "one\r\ntwo", string.Empty));
        var reader = ReaderFor(runner);

        OpenSshClientCapabilities caps = reader.Read(Ssh).Capabilities!;

        Assert.False(caps.Macs.IsKnown);
        Assert.Empty(caps.Macs.Names);
        Assert.True(caps.Kex.IsKnown);

        OpenSshAlgorithmSelection selection = caps.IntersectMacs(new[] { "hmac-sha2-256", "x" });
        Assert.False(selection.Verified);
        Assert.Equal(new[] { "hmac-sha2-256", "x" }, selection.Algorithms);
    }

    [Fact]
    public void Intersect_KeepsRequestedOrderAndDropsUnknownNames()
    {
        var reader = ReaderFor(RunnerFor("OpenSSH_for_Windows_9.5p1"));
        OpenSshClientCapabilities caps = reader.Read(Ssh).Capabilities!;

        OpenSshAlgorithmSelection selection = caps.IntersectKex(new[] { "gamma", "mlkem768x25519-sha256", "alpha" });

        Assert.True(selection.Verified);
        Assert.Equal(new[] { "gamma", "alpha" }, selection.Algorithms);
    }

    [Theory]
    [InlineData(7, 7, false, false)]
    [InlineData(7, 8, true, false)]
    [InlineData(8, 6, true, false)]
    [InlineData(8, 7, true, true)]
    [InlineData(9, 5, true, true)]
    public void FeatureGates_FollowTheVersionThresholds(int major, int minor, bool setEnv, bool tokenizer)
    {
        var caps = new OpenSshClientCapabilities(
            Ssh,
            new OpenSshVersion(major, minor, 1),
            OpenSshAlgorithmList.Unknown,
            OpenSshAlgorithmList.Unknown,
            OpenSshAlgorithmList.Unknown,
            OpenSshAlgorithmList.Unknown);

        Assert.Equal(setEnv, caps.SupportsSetEnv);
        Assert.Equal(tokenizer, caps.UsesQuotedTokenizer);
    }

    [Fact]
    public void ResolveAttested_SkipsRelativePathEntriesAndNonWindowsBuilds()
    {
        string msysDir = @"C:\Msys\bin";
        string goodDir = @"C:\Good\bin";
        string msysExe = Path.Combine(msysDir, "ssh.exe");
        string goodExe = Path.Combine(goodDir, "ssh.exe");
        string relativeExe = Path.Combine("relative", "ssh.exe");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { msysExe, goodExe, relativeExe };
        var runner = new FakeRunner((exe, args, _) =>
        {
            Assert.NotEqual(relativeExe, exe);
            string banner = exe == msysExe ? "OpenSSH_9.9p2, OpenSSL 3.2.2" : "OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2";
            return args[0] == "-V"
                ? new OpenSshProcessResult(0, string.Empty, banner)
                : new OpenSshProcessResult(0, "alpha", string.Empty);
        });

        OpenSshResolution result = OpenSshClientLocator.ResolveAttested(
            null,
            ReaderFor(runner),
            existing.Contains,
            @"C:\NoWin",
            string.Join(Path.PathSeparator, "relative", msysDir, goodDir));

        Assert.Equal(goodExe, result.Path);
    }

    [Fact]
    public void ResolveAttested_ConfiguredPathIsAttestedAndNeverReplaced()
    {
        string custom = Path.Combine(WindowsRoot, "custom-ssh.exe");
        var reader = ReaderFor(RunnerFor("OpenSSH_9.9p2, OpenSSL 3.2.2"));

        OpenSshResolution result = OpenSshClientLocator.ResolveAttested(
            custom,
            reader,
            _ => true,
            @"C:\Win",
            @"C:\Good\bin");

        Assert.False(result.IsAccepted);
        Assert.Equal(OpenSshRefusalReason.NotWindowsBuild, result.Refusal);
    }

    [Fact]
    public void ResolveAttested_ReportsNotFoundWhenNothingExists()
    {
        OpenSshResolution result = OpenSshClientLocator.ResolveAttested(
            null,
            ReaderFor(RunnerFor("OpenSSH_for_Windows_9.5p1")),
            _ => false,
            @"C:\Win",
            @"C:\Good\bin");

        Assert.Equal(OpenSshRefusalReason.NotFound, result.Refusal);
    }

    [Fact]
    public void RealInboxSsh_IsAttestedWhenInstalled()
    {
        string inbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        if (!File.Exists(inbox))
        {
            return;
        }

        OpenSshResolution result = new OpenSshCapabilitiesReader().Read(inbox);

        Assert.True(result.IsAccepted, "Inbox OpenSSH should report an OpenSSH_for_Windows build.");
        Assert.True(result.Capabilities!.Version.Major >= 7);
    }
}
