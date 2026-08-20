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

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes how a complete copy of a <c>ServerProfileDto</c> is produced across the repository.
/// </summary>
/// <remarks>
/// A profile carries presence flags that record which optional fields the file it came from
/// actually declared. Those flags are not serialized, so a JSON round-trip does not reproduce them:
/// deserializing raises them unconditionally. Copies made that way claimed fields their source
/// never had, which turned off the legacy SSH credential mapping and skipped the legacy RDP
/// resolution migration. Every complete copy now goes through the single fidelity primitive.
/// </remarks>
public sealed class CloneFidelityConventionGuardTests
{
    /// <summary>
    /// Production files that make a complete copy of a profile, each of which must route through
    /// the primitive.
    /// </summary>
    private static readonly string[] CompleteCopyCallSites =
    [
        Path.Combine("Heimdall.App", "Services", "EmbeddedSessionManager.cs"),
        Path.Combine("Heimdall.App", "Services", "SplitService.cs"),
        Path.Combine("Heimdall.App", "Services", "Import", "ProfileImportService.cs"),
        Path.Combine("Heimdall.App", "ViewModels", "Session", "SessionCoordinator.cs"),
        Path.Combine("Heimdall.App", "ViewModels", "ServerListViewModel.Bulk.cs")
    ];

    /// <summary>
    /// Production files that make a complete copy of an SSH gateway.
    /// </summary>
    /// <remarks>
    /// A gateway carries the same setter-raised presence flag as a profile, and the same derived
    /// credential mapping hangs off it. The profile guard could not see these: it looks for a
    /// profile round trip by name, and gateways were never copied through one - they were copied
    /// field by field, which raises the flag just as surely. Only the accident that every published
    /// settings object is laundered through a round trip of its own kept the fabrication from
    /// reaching a connect path.
    /// </remarks>
    /// <remarks>
    /// Each site carries its OWN obligation, not a choice between the two primitives. The settings
    /// copy must keep the gateway whole; the import reconciler must drop the secrets. Accepting
    /// either at either site let a mutation swap them and go unnoticed.
    /// </remarks>
    private static readonly (string RelativePath, string RequiredPrimitive)[] GatewayCompleteCopyCallSites =
    [
        (Path.Combine("Heimdall.App", "ViewModels", "SettingsViewModel.cs"), "CloneFaithfully()"),
        (Path.Combine("Heimdall.App", "Services", "Import", "GatewayImportReconciler.cs"), "CloneWithoutSecrets()")
    ];

    [Fact]
    public void EveryGatewayCopyCallSiteRoutesThroughThePrimitive()
    {
        string sourceRoot = Path.Combine(FindRepoRoot(), "src");
        List<string> missing = [];

        foreach ((string relativePath, string requiredPrimitive) in GatewayCompleteCopyCallSites)
        {
            string file = Path.Combine(sourceRoot, relativePath);
            Assert.True(File.Exists(file), $"Expected call site no longer exists: {file}");

            if (!File.ReadAllText(file).Contains(requiredPrimitive, StringComparison.Ordinal))
            {
                missing.Add($"{relativePath}: expected {requiredPrimitive}");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// No copy of a gateway may assign its passphrase property, because assigning it is what
    /// fabricates the declaration.
    /// </summary>
    /// <remarks>
    /// Stronger than the call-site check above, and independent of it: a file could route one copy
    /// through the primitive and still hand-write another. Constructions from a foreign source are
    /// a different act and are allowed - an imported or dialog-created gateway legitimately decides
    /// its own fields - so only the two copy sites are swept.
    /// </remarks>
    [Fact]
    public void NoGatewayCopyAssignsThePassphraseProperty()
    {
        string sourceRoot = Path.Combine(FindRepoRoot(), "src");
        List<string> violations = [];

        foreach ((string relativePath, _) in GatewayCompleteCopyCallSites)
        {
            string[] lines = File.ReadAllLines(Path.Combine(sourceRoot, relativePath));
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Contains("SshKeyPassphraseEncrypted =", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void NoProductionCodeClonesAProfileThroughAJsonRoundTrip()
    {
        List<string> violations = [];

        foreach (string file in EnumerateProductionSources())
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index].Contains("Deserialize<ServerProfileDto>", StringComparison.Ordinal))
                {
                    violations.Add($"{file}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryCompleteCopyCallSiteRoutesThroughThePrimitive()
    {
        string sourceRoot = Path.Combine(FindRepoRoot(), "src");
        List<string> missing = [];

        foreach (string relativePath in CompleteCopyCallSites)
        {
            string file = Path.Combine(sourceRoot, relativePath);
            Assert.True(File.Exists(file), $"Expected call site no longer exists: {file}");

            if (!File.ReadAllText(file).Contains("CloneFaithfully()", StringComparison.Ordinal))
            {
                missing.Add(file);
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// Guards the guard: the scan has to actually read production sources, so an empty sweep cannot
    /// be mistaken for a clean one.
    /// </summary>
    [Fact]
    public void TheScanReachesProductionSources()
    {
        List<string> files = EnumerateProductionSources().ToList();

        Assert.NotEmpty(files);
        Assert.Contains(
            files,
            file => file.EndsWith(Path.Combine("Configuration", "ServerProfileDto.cs"), StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateProductionSources()
    {
        string sourceRoot = Path.Combine(FindRepoRoot(), "src");

        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
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
