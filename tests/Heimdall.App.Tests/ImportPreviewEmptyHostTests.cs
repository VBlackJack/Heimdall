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
/// A session with no host of its own shows no endpoint in the import preview, not a bare port.
/// </summary>
public sealed class ImportPreviewEmptyHostTests
{
    [Theory]
    [InlineData("LOCAL")]
    [InlineData("Local")]
    [InlineData("CITRIX")]
    [InlineData("Citrix")]
    public async Task HostlessSession_HasAnEmptyTargetHost(string connectionType)
    {
        var profile = new ServerProfileDto { ConnectionType = connectionType, RemoteServer = "" };

        Assert.Equal("", await TargetHostOfAsync(profile));
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
