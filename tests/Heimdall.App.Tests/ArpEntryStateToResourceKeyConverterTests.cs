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

using System.Globalization;
using Heimdall.App.Converters;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

public sealed class ArpEntryStateToResourceKeyConverterTests
{
    private readonly ArpEntryStateToResourceKeyConverter _converter = new();

    [Theory]
    [InlineData(ArpEntryState.New, "SuccessTextBrush")]
    [InlineData(ArpEntryState.Stable, "TextSecondaryBrush")]
    [InlineData(ArpEntryState.Changed, "WarningTextBrush")]
    [InlineData(ArpEntryState.Gone, "ErrorTextBrush")]
    public void Convert_MapsState_ToExpectedResourceKey(ArpEntryState state, string expectedResourceKey)
    {
        var result = _converter.Convert(state, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expectedResourceKey, result);
    }
}
