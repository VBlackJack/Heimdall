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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what a picker row's spoken name is made of: title, risk, platform and category, in that
/// order, the way the library's own rows are named.
/// </summary>
public sealed class CommandLibraryPickerItemNameTests
{
    [Fact]
    public void ARow_NamesTitleRiskPlatformAndCategory()
    {
        CommandLibraryPickerItem item = Item(riskLabel: "High risk");

        Assert.Equal("Restart a service, High risk, LIN, Services", item.AccessibleName);
    }

    /// <summary>
    /// The contract is what the container behaviour looks for; an item without it announces its
    /// class name whatever its name says.
    /// </summary>
    [Fact]
    public void ThePickerItem_IsEligibleForTheContainerBehaviour()
    {
        object item = Item(riskLabel: "High risk");

        Assert.IsAssignableFrom<IAccessibleItemViewModel>(item);
    }

    [Fact]
    public void ARowWithoutARiskLabel_LeavesNoEmptySlot()
    {
        CommandLibraryPickerItem item = Item(riskLabel: string.Empty);

        Assert.Equal("Restart a service, LIN, Services", item.AccessibleName);
    }

    private static CommandLibraryPickerItem Item(string riskLabel) => new()
    {
        ActionId = "restart-service",
        Title = "Restart a service",
        Category = "Services",
        HasLinuxTemplate = true,
        HasWindowsTemplate = false,
        LinuxParameters = [],
        PlatformBadge = "LIN",
        Presentation = new CommandPresentation
        {
            Title = "Restart a service",
            Description = "Restarts a systemd unit and shows its status afterwards.",
            Notes = string.Empty,
            Level = CriticalityLevel.Dangerous,
            Platform = Platform.Linux,
            Examples = [],
            Links = [],
            RiskLabel = riskLabel,
            RiskBadge = "!",
            RiskBrushKey = "RiskDangerousBrush",
            PlatformLabel = "LIN",
        },
    };
}
