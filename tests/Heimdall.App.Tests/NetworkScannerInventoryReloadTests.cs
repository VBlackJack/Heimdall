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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The network scanner adds sessions; it must refresh the session list without reseeding the
/// Settings panel from disk, which would discard the user's unsaved Settings edits.
/// </summary>
public sealed class NetworkScannerInventoryReloadTests
{
    private const string Signature =
        "private static async Task ApplyNetworkScanResultAsync(MainViewModel vm, NetworkScanResult result)";

    [Fact]
    public void ScannerAdditions_ReloadTheSessionListOnly()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(Path.Combine(
                ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml.cs"))),
            Signature);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "await vm.ReloadServerInventoryAsync();"),
            "the network scanner no longer refreshes the session list after adding sessions");
        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                logic,
                "await vm.ReloadConfigurationAsync(await vm.ConfigManager.LoadSettingsAsync());"),
            "the network scanner reloads the whole configuration, which reseeds the Settings "
                + "panel from disk and discards its unsaved edits");
    }
}
