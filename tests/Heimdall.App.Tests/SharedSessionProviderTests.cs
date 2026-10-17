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
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class SharedSessionProviderTests : IDisposable
{
    private const string SourceId = "teamfile1";
    private const string ResolvedLogin = "jdoe";

    // Anonymized: the structure of a team file, fake hosts only.
    private const string Content = """
        [Bookmarks_1]
        SubRep=PROD
        ImgNum=41
        srv-db01=#126#0%srv-db01.example.test%22%[DAS]%%0%-1%sudo su - www%gw1.example.test%22%[DAS]%-1#MobaFont%10#0#database
        srv-tools01=#140#0%srv-tools01.example.test%22%[DAS]%%0%-1%%%%%-1#MobaFont%10#0#tools
        [SharedSettings]
        AllowedEditors=editor-a
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-shared-source-tests",
        Guid.NewGuid().ToString("N"));

    public SharedSessionProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LoadAsync_ValidFile_HoldsSanitizedProfilesAndGatewaysInTheSourceNamespace()
    {
        string path = WriteSource("team.mxtsessions");
        SharedSessionProvider provider = new(await LocalizerAsync());

        await provider.LoadAsync(Settings(Source(SourceId, path)));

        SharedSessionSourceState state = Assert.Single(provider.Sources);
        Assert.Equal(SharedSessionSourceStatus.Loaded, state.Status);
        Assert.Equal(2, state.ProfileCount);
        Assert.Equal(1, state.GatewayCount);
        Assert.Null(state.ErrorMessage);

        string prefix = SharedSessionIds.PrefixFor(SourceId);
        Assert.All(provider.Profiles, profile => Assert.StartsWith(prefix, profile.Id, StringComparison.Ordinal));
        SshGatewayDto gateway = Assert.Single(provider.Gateways);
        Assert.StartsWith(prefix, gateway.Id, StringComparison.Ordinal);
        Assert.Equal(ResolvedLogin, gateway.User);

        ServerProfileDto behindGateway = Assert.Single(provider.Profiles, profile => profile.DisplayName == "srv-db01");
        Assert.Equal(gateway.Id, behindGateway.SshGatewayId);
        Assert.Equal(ResolvedLogin, behindGateway.SshUsername);
        Assert.Equal("sudo su - www", Assert.Single(behindGateway.PostConnectSteps).Input);
        Assert.False(behindGateway.ExecutionConfirmed);
        Assert.Null(behindGateway.SshKeyPath);
        Assert.Equal("Embedded", behindGateway.SshMode);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_IsReportedAndTheOtherSourcesStillLoad()
    {
        LocalizationManager localizer = await LocalizerAsync();
        string missing = Path.Combine(_directory, "absent.mxtsessions");
        string present = WriteSource("team.mxtsessions");
        SharedSessionProvider provider = new(localizer);

        await provider.LoadAsync(Settings(Source("missing1", missing), Source(SourceId, present)));

        SharedSessionSourceState missingState = provider.Sources.Single(state => state.SourceId == "missing1");
        Assert.Equal(SharedSessionSourceStatus.Error, missingState.Status);
        Assert.Equal(localizer.Format("SharedSessionSourceErrorMissing", missing), missingState.ErrorMessage);
        Assert.Equal(SharedSessionSourceStatus.Loaded, provider.Sources.Single(state => state.SourceId == SourceId).Status);
        Assert.Equal(2, provider.Profiles.Count);
    }

    [Fact]
    public async Task LoadAsync_UnreadableFile_IsReportedNotThrown()
    {
        LocalizationManager localizer = await LocalizerAsync();
        string path = WriteSource("locked.mxtsessions");
        SharedSessionProvider provider = new(localizer);

        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await provider.LoadAsync(Settings(Source(SourceId, path)));
        }

        SharedSessionSourceState state = Assert.Single(provider.Sources);
        Assert.Equal(SharedSessionSourceStatus.Error, state.Status);
        Assert.NotNull(state.ErrorMessage);
        Assert.Contains(path, state.ErrorMessage, StringComparison.Ordinal);
        Assert.NotEqual(localizer.Format("SharedSessionSourceErrorMissing", path), state.ErrorMessage);
        Assert.Empty(provider.Profiles);
    }

    [Fact]
    public async Task LoadAsync_DisabledSource_ReadsNothing()
    {
        SharedSessionProvider provider = new(await LocalizerAsync());
        SharedSessionSourceDto source = Source(SourceId, Path.Combine(_directory, "absent.mxtsessions"));
        source.Enabled = false;

        await provider.LoadAsync(Settings(source));

        Assert.Equal(SharedSessionSourceStatus.Disabled, Assert.Single(provider.Sources).Status);
        Assert.Empty(provider.Profiles);
    }

    [Fact]
    public async Task LoadAsync_PathWithEnvironmentVariable_IsExpanded()
    {
        string variable = "HEIMDALL_SHARED_SOURCE_" + Guid.NewGuid().ToString("N");
        string path = WriteSource("team.mxtsessions");
        Environment.SetEnvironmentVariable(variable, _directory);
        try
        {
            SharedSessionProvider provider = new(await LocalizerAsync());

            await provider.LoadAsync(Settings(Source(SourceId, $"%{variable}%\\team.mxtsessions")));

            SharedSessionSourceState state = Assert.Single(provider.Sources);
            Assert.Equal(SharedSessionSourceStatus.Loaded, state.Status);
            Assert.Equal(path, state.ExpandedPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task LoadAsync_InvalidOrDuplicateSourceId_IsAnError()
    {
        string path = WriteSource("team.mxtsessions");
        SharedSessionProvider provider = new(await LocalizerAsync());

        await provider.LoadAsync(Settings(Source("Not-Valid", path), Source(SourceId, path), Source(SourceId, path)));

        Assert.Equal(
            new[] { SharedSessionSourceStatus.Error, SharedSessionSourceStatus.Loaded, SharedSessionSourceStatus.Error },
            provider.Sources.Select(state => state.Status));
        Assert.Equal(2, provider.Profiles.Count);
    }

    [Fact]
    public async Task Profiles_AreCopies_SoAnEditChangesNothingHeld()
    {
        SharedSessionProvider provider = new(await LocalizerAsync());
        await provider.LoadAsync(Settings(Source(SourceId, WriteSource("team.mxtsessions"))));
        ServerProfileDto handedOut = provider.Profiles[0];

        handedOut.RemoteServer = "elsewhere.example.test";
        handedOut.PostConnectSteps[0].Input = "rm -rf /";

        ServerProfileDto again = provider.FindProfile(handedOut.Id)!;
        Assert.NotEqual("elsewhere.example.test", again.RemoteServer);
        Assert.NotEqual("rm -rf /", again.PostConnectSteps[0].Input);
    }

    [Fact]
    public async Task LoadAsync_NeverWritesTheSourceFile()
    {
        string path = WriteSource("team.mxtsessions");
        byte[] before = await File.ReadAllBytesAsync(path);
        DateTime writtenBefore = File.GetLastWriteTimeUtc(path);
        SharedSessionProvider provider = new(await LocalizerAsync());

        await provider.LoadAsync(Settings(Source(SourceId, path)));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task LoadAsync_Again_ReplacesWhatIsHeld()
    {
        SharedSessionProvider provider = new(await LocalizerAsync());
        await provider.LoadAsync(Settings(Source(SourceId, WriteSource("team.mxtsessions"))));
        Assert.NotEmpty(provider.Profiles);

        await provider.LoadAsync(Settings());

        Assert.Empty(provider.Profiles);
        Assert.Empty(provider.Gateways);
        Assert.Empty(provider.Sources);
    }

    private string WriteSource(string fileName)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, Content);
        return path;
    }

    private static SharedSessionSourceDto Source(string id, string path) =>
        new() { Id = id, Name = "Team sessions", Path = path, Enabled = true };

    private static AppSettings Settings(params SharedSessionSourceDto[] sources) => new()
    {
        SharedSessionSources = [.. sources],
        MobaXtermUserVariables = new Dictionary<string, string> { ["DAS"] = ResolvedLogin },
    };

    private static async Task<LocalizationManager> LocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
