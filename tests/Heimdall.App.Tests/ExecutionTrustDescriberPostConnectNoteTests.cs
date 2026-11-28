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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Every post-connect step stays listed whatever the connection type, because the one Allow flag
/// covers them; a type that never types them gets one closing line saying so.
/// </summary>
public sealed class ExecutionTrustDescriberPostConnectNoteTests
{
    private static readonly IReadOnlyDictionary<string, string> NoTitles =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task Describe_LocalWithStep_EndsWithTheNotRunNote()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        ServerProfileDto profile = CreateProfile("LOCAL", enabled: true);
        profile.LocalShellExecutable = "tool.exe";

        string[] rows = Rows(ExecutionTrustDescriber.Describe(profile, NoTitles, localizer));

        Assert.Equal(3, rows.Length);
        Assert.Contains("tool.exe", rows[0], StringComparison.Ordinal);
        Assert.Contains("whoami", rows[1], StringComparison.Ordinal);
        Assert.Equal(localizer["ServerDialogExecutionTrustPostConnectNotRun"], rows[2]);
    }

    [Fact]
    public async Task Describe_SshWithStep_HasNoNote()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();

        string[] rows = Rows(
            ExecutionTrustDescriber.Describe(CreateProfile("SSH", enabled: true), NoTitles, localizer));

        string row = Assert.Single(rows);
        Assert.Contains("whoami", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_CitrixWithStep_ListsIcaRowStepRowThenNote()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        ServerProfileDto profile = CreateProfile("CITRIX", enabled: true);
        profile.CitrixIcaFilePath = "C:\\files\\app.ica";

        string[] rows = Rows(ExecutionTrustDescriber.Describe(profile, NoTitles, localizer));

        Assert.Equal(3, rows.Length);
        Assert.Contains("app.ica", rows[0], StringComparison.Ordinal);
        Assert.Contains("whoami", rows[1], StringComparison.Ordinal);
        Assert.Equal(localizer["ServerDialogExecutionTrustPostConnectNotRun"], rows[2]);
    }

    [Fact]
    public async Task Describe_LocalWithOnlyADisabledStep_HasNoNote()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        ServerProfileDto profile = CreateProfile("LOCAL", enabled: false);
        profile.LocalShellExecutable = "tool.exe";

        string[] rows = Rows(ExecutionTrustDescriber.Describe(profile, NoTitles, localizer));

        string row = Assert.Single(rows);
        Assert.Contains("tool.exe", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_SshWithStartupCommandAndEnvVars_ListsBothPayloads()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        ServerProfileDto profile = new()
        {
            Id = "profile-ssh-startup",
            DisplayName = "Imported SSH",
            ConnectionType = "SSH",
            RemoteServer = "ops.example.com",
            SshStartupCommand = "tmux new -As ops",
            SshEnvironmentVariables = ["APP_ENV=prod"]
        };

        string[] rows = Rows(ExecutionTrustDescriber.Describe(profile, NoTitles, localizer));

        Assert.Equal(2, rows.Length);
        Assert.Contains("tmux new -As ops", rows[0], StringComparison.Ordinal);
        Assert.Contains("APP_ENV=prod", rows[1], StringComparison.Ordinal);
    }

    private static string[] Rows(string described) =>
        described.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static ServerProfileDto CreateProfile(string connectionType, bool enabled) => new()
    {
        Id = "profile-1",
        DisplayName = "Imported",
        ConnectionType = connectionType,
        RemoteServer = "host",
        PostConnectSteps = [new PostConnectStep { Input = "whoami", Enabled = enabled }],
    };

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
