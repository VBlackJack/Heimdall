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
/// "Go to gateway settings" lands on the gateways, not on whichever SSH sub-tab was shown last.
/// </summary>
/// <remarks>
/// A source reading: the handler belongs to the main window, which cannot be built here.
/// </remarks>
public sealed class GatewaySettingsNavigationSourceTests
{
    private const string HandlerMember = "private void OnNavigateToGatewaySettings(object sender, RoutedEventArgs e)";

    private const string SelectSshTab = "Mw_SettingsSubTabControl.SelectedItem = Mw_SettingsTabSsh;";

    private const string SelectGatewaysSubTab = "Mw_SettingsSshSubTabControl.SelectedItem = Mw_SettingsSshSubTabGateways;";

    [Fact]
    public void NavigatingToGatewaySettings_OpensTheGatewaysSubTab()
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml.cs");
        Assert.True(File.Exists(full), $"Source not found: {full}");
        string logic = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(full)), HandlerMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SelectSshTab), "the handler does not open the SSH tab");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SelectGatewaysSubTab), "the handler does not open the gateways sub-tab");
    }
}
