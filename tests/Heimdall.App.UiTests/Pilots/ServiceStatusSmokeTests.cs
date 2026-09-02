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

using FlaUI.Core.AutomationElements;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels.Tools;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests.Pilots;

[Collection(DesktopUiCollection.Name)]
public sealed class ServiceStatusSmokeTests : UiTestBase<ServiceStatusView>
{
    private static readonly TimeSpan InitialRefreshTimeout = TimeSpan.FromSeconds(30);

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void Loads_InitialRefresh_PopulatesStats()
    {
        using var session = OpenTool();

        WaitForRefreshSnapshot(session);
        Assert.NotEqual("-", ReadText(session.FindByAutomationId("ServiceStatus.TotalCount")));
        Assert.NotEqual("-", ReadText(session.FindByAutomationId("ServiceStatus.RunningCount")));
        Assert.NotEqual("-", ReadText(session.FindByAutomationId("ServiceStatus.StoppedCount")));
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void FilterInput_RestrictsDisplayedServices()
    {
        using var session = OpenTool();

        WaitForRefreshSnapshot(session);
        Assert.True(session.InvokeOnUi(control => ((ServiceStatusViewModel)control.DataContext!).DisplayedServices.Count > 0));

        session.FindByAutomationId("ServiceStatus.FilterInput").AsTextBox().Text = "__no_service_should_match__";
        WaitHelpers.WaitUntil(
            () => session.InvokeOnUi(control => ((ServiceStatusViewModel)control.DataContext!).DisplayedServices.Count == 0),
            "service status filtered to zero");

        session.FindByAutomationId("ServiceStatus.FilterInput").AsTextBox().Text = string.Empty;
        WaitHelpers.WaitUntil(
            () => session.InvokeOnUi(control =>
            {
                var vm = (ServiceStatusViewModel)control.DataContext!;
                return vm.DisplayedServices.Count == vm.TotalCount;
            }),
            "service status filter reset");
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void CopyButton_CopiesDisplayedServices()
    {
        using var session = OpenTool();

        WaitForRefreshSnapshot(session);
        WaitHelpers.WaitUntil(
            () => session.FindByAutomationId("ServiceStatus.CopyButton").AsButton().IsEnabled,
            "service status copy enabled",
            TimeSpan.FromSeconds(5));

        var firstName = session.InvokeOnUi(control => ((ServiceStatusViewModel)control.DataContext!).DisplayedServices[0].Name);
        session.FindByAutomationId("ServiceStatus.CopyButton").AsButton().Invoke();

        WaitHelpers.WaitUntilTextMatches(
            ReadClipboardText,
            value => value.Contains(firstName, StringComparison.OrdinalIgnoreCase) && value.Contains('\t'),
            "service status clipboard");
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void HelpButton_TogglesHelpPanel()
    {
        using var session = OpenTool();

        session.FindByAutomationId("ServiceStatus.HelpButton").AsButton().Invoke();
        var closeButton = session.FindByAutomationId("ServiceStatus.CloseHelpButton");
        WaitHelpers.WaitUntilVisible(closeButton, "service status help panel visible");

        closeButton.AsButton().Invoke();
        WaitHelpers.WaitUntil(
            () => IsCollapsed(session.TryFindByAutomationId("ServiceStatus.CloseHelpButton")),
            "service status help panel collapsed");
    }

    private static void WaitForRefreshSnapshot(HostedToolWindow<ServiceStatusView> session)
    {
        WaitHelpers.WaitUntil(
            () => session.InvokeOnUi(control => ((ServiceStatusViewModel)control.DataContext!).HasRefreshSnapshot),
            "service status initial refresh",
            InitialRefreshTimeout);
    }
}
