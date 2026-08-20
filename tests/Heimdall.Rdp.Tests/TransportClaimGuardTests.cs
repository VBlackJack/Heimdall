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

namespace Heimdall.Rdp.Tests;

/// <summary>
/// Keeps the RDP project from claiming a transport guarantee it cannot deliver.
/// </summary>
/// <remarks>
/// The client exposes no way for an application to disable UDP transport; only the
/// <c>fClientDisableUDP</c> machine policy does. Heimdall's option suppresses the probe, which is
/// what times out behind a firewall that drops UDP, and that is all it does. The claim that it
/// forces TCP was corrected in the label a user reads and then survived in the code for another
/// release, which is why it is now a guard rather than a comment.
/// </remarks>
public sealed class TransportClaimGuardTests
{
    /// <summary>
    /// The noun phrase the overclaim was always carried by, and nothing wider.
    /// </summary>
    /// <remarks>
    /// A first version of this list also forbade "force TCP", which matched the sentences that
    /// explain the option does not force TCP. A guard that cannot tell a claim from its denial
    /// pushes the next author to stop explaining, so it forbids only the form the claim took:
    /// "TCP-only mode", "forcing TCP-only RDP connections", "Force TCP-only".
    /// </remarks>
    private static readonly string[] ForbiddenClaims =
    [
        "TCP-only",
        "TCP only",
    ];

    [Fact]
    public void NoSourceInTheRdpProjectClaimsToForceTcp()
    {
        List<string> violations = [];

        foreach (string file in EnumerateProjectSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (string claim in ForbiddenClaims)
                {
                    if (lines[index].Contains(claim, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{file}:{index + 1}: {lines[index].Trim()}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Guards the guard: an empty sweep would otherwise read as a clean one.
    /// </summary>
    [Fact]
    public void TheSweepReachesTheProjectSources()
    {
        List<string> files = EnumerateProjectSources().ToList();

        Assert.NotEmpty(files);
        Assert.Contains(
            files,
            file => file.EndsWith("RdpRedirectionOptions.cs", StringComparison.Ordinal));
        Assert.Contains(
            files,
            file => file.EndsWith("RdpFileGenerator.cs", StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateProjectSources()
    {
        string projectRoot = Path.Combine(FindRepoRoot(), "src", "Heimdall.Rdp");

        return Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
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
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
