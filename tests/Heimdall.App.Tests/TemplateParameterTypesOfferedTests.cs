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

using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that the action editor offers every type the generator understands.
/// </summary>
/// <remarks>
/// The editor offered seven of the eleven accepted names: "number" and "driveletter" were
/// validated by the generator and shipped in seeds, but an operator writing an action could
/// not pick them, and an imported action using one showed a blank type box.
/// </remarks>
public sealed class TemplateParameterTypesOfferedTests
{
    [Fact]
    public void TheEditorOffersEveryAcceptedTypeButTheLegacySpellings()
    {
        var expected = TemplateParameterTypes.Accepted
            .Except([TemplateParameterTypes.Integer, TemplateParameterTypes.Boolean], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, TemplateParameterTypes.Offered.Order(StringComparer.Ordinal));
        Assert.Equal(TemplateParameterTypes.Offered, ParameterEntryVm.AvailableTypes);
    }

    [Fact]
    public void NumberAndDriveLetterFollowIntInTheEditorsList()
    {
        var offered = TemplateParameterTypes.Offered.ToList();
        var integer = offered.IndexOf(TemplateParameterTypes.Int);

        Assert.Equal(TemplateParameterTypes.Number, offered[integer + 1]);
        Assert.Equal(TemplateParameterTypes.DriveLetter, offered[integer + 2]);
    }
}
