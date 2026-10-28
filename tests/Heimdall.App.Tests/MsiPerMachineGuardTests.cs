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
using System.Xml.Linq;

namespace Heimdall.App.Tests;

/// <summary>
/// A per-machine MSI keeps its components' key paths under HKLM.
/// </summary>
/// <remarks>
/// A key path under HKCU exists only for the account that ran the install. For every other
/// account the component reads as missing, so a repair or an advertised shortcut misfires.
/// The MSI is not built in the test lane; this reads the WiX source instead, and the
/// packaging workflow builds the package itself.
/// </remarks>
public sealed class MsiPerMachineGuardTests
{
    private const string ProductPath = "installer/Product.wxs";
    private const string PerMachine = "perMachine";
    private const string PerUserHive = "HKCU";

    /// <summary>The registry rows the package carries today (one per shortcut component).</summary>
    private const int MinimumRegistryRows = 2;

    [Fact]
    public void PerMachinePackage_HasNoRegistryRowUnderThePerUserHive()
    {
        XDocument product = XDocument.Load(Path.Combine(RepositoryRoot(), ProductPath));

        Assert.Equal(PerMachine, Descendants(product, "Package").Single().Attribute("Scope")?.Value);

        List<XElement> rows = [.. Descendants(product, "RegistryValue"), .. Descendants(product, "RegistryKey")];

        // A scan that found no registry row would pass by having nothing to check.
        Assert.True(
            rows.Count >= MinimumRegistryRows,
            $"Only {rows.Count} registry rows were found; the scan did not reach the components.");

        List<string> perUser = [.. rows
            .Where(row => string.Equals(row.Attribute("Root")?.Value, PerUserHive, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Attribute("Name")?.Value ?? row.Attribute("Key")?.Value ?? "?")];

        Assert.True(
            perUser.Count == 0,
            "These registry rows of a per-machine package are under HKCU, so the component is "
            + "missing for every account but the installing one: " + string.Join(", ", perUser));
    }

    private static IEnumerable<XElement> Descendants(XDocument document, string localName)
        => document.Descendants().Where(element => element.Name.LocalName == localName);

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
