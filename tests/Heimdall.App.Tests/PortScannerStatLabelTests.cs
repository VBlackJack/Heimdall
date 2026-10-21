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

using Heimdall.App.Views.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// Guards the Port Scanner footer counters. The locale values already end with a
/// colon ("Open:", French "Ouverts :"), and the formatter used to append another
/// one, which printed "Open:: 0". The label must carry exactly one colon whether
/// or not the locale value supplies it.
/// </summary>
public sealed class PortScannerStatLabelTests
{
    [Theory]
    [InlineData("Open:", 0, "Open: 0")]
    [InlineData("Total:", 12, "Total: 12")]
    [InlineData("Ouverts :", 3, "Ouverts : 3")]
    [InlineData("Open", 0, "Open: 0")]
    [InlineData("Closed ", 5, "Closed: 5")]
    public void FormatStatLabel_WritesExactlyOneColon(string label, int value, string expected)
    {
        string formatted = PortScannerView.FormatStatLabel(label, value);

        Assert.Equal(expected, formatted);
        Assert.Equal(1, formatted.Count(c => c == ':'));
    }
}
