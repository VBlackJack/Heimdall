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
/// The command palette never decides what a row is by the text of its identifier.
/// </summary>
/// <remarks>
/// <para>Routing by the identifier's prefix is the shape that kept the quick-connect collision
/// alive: the same string can be a saved profile and a typed destination, and no examination
/// of it separates them. The behavioural tests cover the three entry points a user reaches;
/// the split-mode branches share the same predicate, and this guard is what keeps a fresh
/// prefix test out of any of them, in the file where the routing lives and in its partials.</para>
/// <para>Minting an identifier with the prefix stays allowed: the trust store keys on it and the
/// import doors reserve it. Only the predicate that reads it back is out.</para>
/// </remarks>
public sealed class PaletteRoutingByProvenanceGuardTests
{
    private const string PaletteRelativePath = "src/Heimdall.App/ViewModels/CommandPalette";
    private const string PaletteFilePattern = "CommandPaletteViewModel*.cs";
    private const string PrefixPredicate = "AdHocProfileIds.IsAdHoc(";

    [Fact]
    public void ThePalette_NeverRoutesByTheIdentifierPrefix()
    {
        string directory = Path.Combine(ViewSource.RepoRoot(), PaletteRelativePath);
        List<string> files = [.. Directory.EnumerateFiles(directory, PaletteFilePattern)];

        // A guard that scans nothing passes for free.
        Assert.NotEmpty(files);

        // An absence assertion, in the shape SourceReadingAssertionGuardTests recognises as one:
        // folding a prefix test behind a false term keeps its text and keeps this red, which is
        // the safe direction for a guard that forbids a shape.
        foreach (string file in files)
        {
            string logic = ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(file));
            Assert.False(
                logic.Contains(PrefixPredicate, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} reads the identifier prefix to route a row. "
                + "Route on ServerItemViewModel.IsTypedDestination instead.");
        }
    }
}
