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
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The import preview shows the port of the session's own protocol.
/// </summary>
public sealed class ImportPreviewTargetPortTests
{
    private const string Host = "srv.example.test";

    [Fact]
    public async Task SshCandidate_ShowsItsSshPort()
    {
        var profile = new ServerProfileDto
        {
            ConnectionType = "SSH",
            RemoteServer = Host,
            RemotePort = 3389,
            SshPort = 2222
        };

        Assert.Equal($"{Host}:2222", await TargetHostOfAsync(profile));
    }

    [Fact]
    public async Task RdpCandidate_StillShowsItsRemotePort()
    {
        var profile = new ServerProfileDto
        {
            ConnectionType = "RDP",
            RemoteServer = Host,
            RemotePort = 3389,
            SshPort = 2222
        };

        Assert.Equal($"{Host}:3389", await TargetHostOfAsync(profile));
    }

    [Fact]
    public async Task VncCandidate_ShowsItsVncPort()
    {
        var profile = new ServerProfileDto
        {
            ConnectionType = "VNC",
            RemoteServer = Host,
            RemotePort = 3389,
            VncPort = 5901
        };

        Assert.Equal($"{Host}:5901", await TargetHostOfAsync(profile));
    }

    private static async Task<string> TargetHostOfAsync(ServerProfileDto profile)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        var row = new RdpImportRowViewModel(
            new RdpImportPreviewEntry
            {
                SourceFilePath = "C:\\profiles.json",
                ProposedName = "Imported",
                Candidate = profile
            },
            localizer);
        return row.TargetHost;
    }
}
