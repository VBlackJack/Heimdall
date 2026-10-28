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

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins every vendored component under <c>src/Heimdall.App/Assets</c> (plink, gsudo, noVNC, pako,
/// xterm and its add-ons, the draw.io tree, the built Milkdown bundle) to the version and
/// SHA-256 recorded in the <c>VENDORED.json</c> that sits beside its <c>VENDORED.md</c>.
/// </summary>
/// <remarks>
/// <para>These files arrive from outside the NuGet graph, so no restore, lock file or
/// vulnerability scan ever looks at them. A <c>VENDORED.md</c> says what was meant; nothing
/// said what is there. A binary swapped, an editor bundle rebuilt from a different lock or a
/// script patched in place left every test green. This test makes each such change visible as
/// a diff of a manifest that a reviewer has to accept.</para>
/// <para>One manifest per directory, beside the notes it belongs to, rather than a single
/// file: the pin and the human-readable version table are read and edited together, a
/// directory's update touches only its own files, and a new vendored directory is found by the
/// presence of its <c>VENDORED.md</c>, so adding one without a manifest fails here.</para>
/// <para>Text files are hashed with CRLF folded to LF. The repository stores LF and the
/// checkout is CRLF on Windows (<c>.gitattributes</c>), so a hash of the working tree bytes
/// would differ between a developer's checkout and another platform's. Binaries are hashed
/// as they are. A directory of many files (draw.io has about 2300, noVNC 50) is pinned by a
/// digest over its sorted <c>path TAB hash</c> lines plus the file count.</para>
/// <para>When a hash is out of date because the component was legitimately updated, the failure
/// message carries the new value to copy into the manifest, after the difference has been
/// reviewed.</para>
/// </remarks>
public sealed class VendoredAssetManifestTests
{
    private const string SolutionFile = "Heimdall.slnx";
    private const string AssetsDirectory = "src/Heimdall.App/Assets";
    private const string NotesFileName = "VENDORED.md";
    private const string ManifestFileName = "VENDORED.json";

    /// <summary>The directories that must be found: a scan that skips one must not pass.</summary>
    private static readonly string[] ExpectedVendoredDirectories =
    [
        "Tools", "vnc", "Terminal", "drawio", "milkdown", "milkdown-editor",
    ];

    /// <summary>A scan that hashed fewer files than draw.io alone holds did not reach it.</summary>
    private const int MinimumFilesHashedInTheRealTree = 2300;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".gif", ".jpg", ".jpeg", ".ico", ".ttf", ".otf", ".woff", ".woff2", ".exe", ".dll", ".zip",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class Manifest
    {
        public List<Component> Components { get; set; } = [];

        public List<string> CompleteDirectories { get; set; } = [];

        public List<string> OwnedFiles { get; set; } = [];
    }

    private sealed class Component
    {
        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public List<PinnedFile> Files { get; set; } = [];

        public PinnedTree? Tree { get; set; }
    }

    private sealed class PinnedFile
    {
        public string Path { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public string? VersionMarker { get; set; }

        public string? FileVersion { get; set; }
    }

    private sealed class PinnedTree
    {
        public string Path { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed record Report(List<string> Problems, int FilesHashed, List<string> Directories);

    [Fact]
    public void EveryVendoredFile_MatchesTheVersionAndSha256OfItsManifest()
    {
        Report report = Verify(Path.Combine(RepositoryRoot(), AssetsDirectory));

        Assert.True(
            report.Problems.Count == 0,
            "Vendored assets do not match their VENDORED.json:" + Environment.NewLine
                + string.Join(Environment.NewLine, report.Problems));
    }

    /// <summary>
    /// The verification above passes on an empty scan. This is what makes it mean something: every
    /// vendored directory was found, and the files of the biggest one were really hashed.
    /// </summary>
    [Fact]
    public void TheScan_FindsEveryVendoredDirectory_AndHashesTheWholeDrawioTree()
    {
        Report report = Verify(Path.Combine(RepositoryRoot(), AssetsDirectory));

        foreach (string expected in ExpectedVendoredDirectories)
        {
            Assert.Contains(expected, report.Directories);
        }

        Assert.True(
            report.FilesHashed >= MinimumFilesHashedInTheRealTree,
            $"Only {report.FilesHashed} file(s) were hashed; the draw.io tree alone holds more than {MinimumFilesHashedInTheRealTree}.");
    }

    /// <summary>
    /// The copy the application loads and the file it is built into are two files with one
    /// content; a rebuild that updates one and not the other would pin both and ship the old one.
    /// </summary>
    [Fact]
    public void TheShippedMilkdownBundle_IsTheBuiltOne()
    {
        string assets = Path.Combine(RepositoryRoot(), AssetsDirectory);

        Assert.Equal(
            HashFile(Path.Combine(assets, "milkdown-editor", "dist", "index.html")),
            HashFile(Path.Combine(assets, "milkdown", "index.html")));
    }

    [Fact]
    public void Hash_IgnoresTheLineEndingsOfText_ButNotOfBinaries()
    {
        using TempDirectory temp = new();
        string lf = temp.Write("a.js", "one\ntwo\n");
        string crlf = temp.Write("b.js", "one\r\ntwo\r\n");
        string other = temp.Write("c.js", "one\r\nTWO\r\n");
        string binaryLf = temp.Write("a.exe", "MZ\nPE\n");
        string binaryCrlf = temp.Write("b.exe", "MZ\r\nPE\r\n");

        Assert.Equal(HashFile(lf), HashFile(crlf));
        Assert.NotEqual(HashFile(lf), HashFile(other));
        Assert.NotEqual(HashFile(binaryLf), HashFile(binaryCrlf));
    }

    [Fact]
    public void Verify_AcceptsAnUntouchedFixture_AndHashesItsFiles()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);

        Report report = Verify(temp.Path);

        Assert.Empty(report.Problems);
        Assert.Contains("vendored", report.Directories);
        Assert.Equal(4, report.FilesHashed);
    }

    [Fact]
    public void Verify_ReportsAFileThatChangedWithoutItsManifestEntry()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        temp.Write("vendored/tool.exe", "MZ changed");

        Report report = Verify(temp.Path);

        Assert.Contains(report.Problems, problem => problem.Contains("tool.exe", StringComparison.Ordinal) && problem.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsATreeThatChangedWithoutItsDigest()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        temp.Write("vendored/lib/a.js", "var a = 2;\n");

        Report report = Verify(temp.Path);

        Assert.Contains(report.Problems, problem => problem.Contains("lib", StringComparison.Ordinal) && problem.Contains("digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsAFileAddedToAVendoredDirectory()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        temp.Write("vendored/dropped-in.dll", "MZ new binary");

        Report report = Verify(temp.Path);

        Assert.Contains(report.Problems, problem => problem.Contains("dropped-in.dll", StringComparison.Ordinal) && problem.Contains("no entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsAFileThatWentMissing()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        File.Delete(Path.Combine(temp.Path, "vendored", "tool.exe"));

        Report report = Verify(temp.Path);

        Assert.Contains(report.Problems, problem => problem.Contains("tool.exe", StringComparison.Ordinal) && problem.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsAVersionTheNotesDoNotState_AndAMarkerTheFileDoesNotCarry()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        temp.Write("vendored/VENDORED.md", "| tool | 9.9.9 |\n");

        Assert.Contains(
            Verify(temp.Path).Problems,
            problem => problem.Contains("1.2.3", StringComparison.Ordinal) && problem.Contains("VENDORED.md", StringComparison.Ordinal));

        using TempDirectory markerless = new();
        BuildFixture(markerless);
        string manifestPath = Path.Combine(markerless.Path, "vendored", ManifestFileName);
        markerless.Write("vendored/VENDORED.json", File.ReadAllText(manifestPath).Replace("// banner 1.2.3", "// banner 7.7.7", StringComparison.Ordinal));

        Assert.Contains(
            Verify(markerless.Path).Problems,
            problem => problem.Contains("banner 7.7.7", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsAVendoredDirectoryWithoutAManifest()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        File.Delete(Path.Combine(temp.Path, "vendored", ManifestFileName));

        Report report = Verify(temp.Path);

        Assert.Contains(report.Problems, problem => problem.Contains(ManifestFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_IgnoresACheckoutsLineEndings()
    {
        using TempDirectory temp = new();
        BuildFixture(temp);
        temp.Write("vendored/lib/a.js", "var a = 1;\r\n// banner 1.2.3\r\n");

        Assert.Empty(Verify(temp.Path).Problems);
    }

    private static void BuildFixture(TempDirectory temp)
    {
        temp.Write("vendored/VENDORED.md", "| tool | 1.2.3 |\n");
        temp.Write("vendored/tool.exe", "MZ original");
        temp.Write("vendored/lib/a.js", "var a = 1;\n// banner 1.2.3\n");
        temp.Write("vendored/lib/b.js", "var b = 1;\n");

        string directory = Path.Combine(temp.Path, "vendored");
        PinnedTree tree = ComputeTree(directory, "lib", []);
        Manifest manifest = new()
        {
            Components =
            [
                new Component
                {
                    Name = "tool",
                    Version = "1.2.3",
                    Files =
                    [
                        new PinnedFile { Path = "tool.exe", Sha256 = HashFile(Path.Combine(directory, "tool.exe")) },
                        new PinnedFile
                        {
                            Path = "lib/a.js",
                            Sha256 = HashFile(Path.Combine(directory, "lib", "a.js")),
                            VersionMarker = "// banner 1.2.3",
                        },
                    ],
                    Tree = tree,
                },
            ],
            CompleteDirectories = ["."],
        };
        temp.Write("vendored/VENDORED.json", JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>Checks every directory of <paramref name="assetsRoot"/> that holds a VENDORED.md.</summary>
    private static Report Verify(string assetsRoot)
    {
        List<string> problems = [];
        List<string> directories = [];
        int hashed = 0;

        foreach (string notesPath in Directory.EnumerateFiles(assetsRoot, NotesFileName, SearchOption.AllDirectories))
        {
            string directory = Path.GetDirectoryName(notesPath)!;
            string label = Path.GetRelativePath(assetsRoot, directory).Replace('\\', '/');
            directories.Add(label);

            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                problems.Add($"{label}: {NotesFileName} has no {ManifestFileName} beside it.");
                continue;
            }

            Manifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)
                    ?? throw new JsonException("empty document");
            }
            catch (JsonException exception)
            {
                problems.Add($"{label}/{ManifestFileName}: unreadable ({exception.Message}).");
                continue;
            }

            string notes = File.ReadAllText(notesPath);
            if (manifest.Components.Count == 0)
            {
                problems.Add($"{label}/{ManifestFileName}: lists no component.");
            }

            HashSet<string> covered = new(StringComparer.Ordinal);
            foreach (Component component in manifest.Components)
            {
                if (string.IsNullOrWhiteSpace(component.Version))
                {
                    problems.Add($"{label}: component '{component.Name}' has no version.");
                }
                else if (!notes.Contains(component.Version, StringComparison.Ordinal))
                {
                    problems.Add($"{label}: component '{component.Name}' is pinned at {component.Version}, which {NotesFileName} does not state.");
                }

                foreach (PinnedFile pinned in component.Files)
                {
                    covered.Add(pinned.Path);
                    hashed += CheckFile(directory, label, component, pinned, problems);
                }

                if (component.Tree is not null)
                {
                    hashed += CheckTree(directory, label, component.Tree, manifest.OwnedFiles, covered, problems);
                }
            }

            foreach (string complete in manifest.CompleteDirectories)
            {
                CheckCoverage(directory, label, complete, manifest.OwnedFiles, covered, problems);
            }
        }

        return new Report(problems, hashed, directories);
    }

    private static int CheckFile(string directory, string label, Component component, PinnedFile pinned, List<string> problems)
    {
        string full = Path.Combine(directory, pinned.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            problems.Add($"{label}/{pinned.Path}: file is missing.");
            return 0;
        }

        string actual = HashFile(full);
        if (!string.Equals(actual, pinned.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{label}/{pinned.Path}: SHA-256 is {actual}, the manifest says {pinned.Sha256} (component '{component.Name}' {component.Version}).");
        }

        if (!string.IsNullOrEmpty(pinned.VersionMarker)
            && !File.ReadAllText(full).Contains(pinned.VersionMarker, StringComparison.Ordinal))
        {
            problems.Add($"{label}/{pinned.Path}: does not carry its version marker '{pinned.VersionMarker}'.");
        }

        if (!string.IsNullOrEmpty(pinned.FileVersion))
        {
            string? reported = FileVersionInfo.GetVersionInfo(full).FileVersion;
            if (reported is null || !reported.Contains(pinned.FileVersion, StringComparison.Ordinal))
            {
                problems.Add($"{label}/{pinned.Path}: file version resource is '{reported}', the manifest expects '{pinned.FileVersion}'.");
            }
        }

        return 1;
    }

    private static int CheckTree(
        string directory,
        string label,
        PinnedTree pinned,
        List<string> ownedFiles,
        HashSet<string> covered,
        List<string> problems)
    {
        string root = pinned.Path == "." ? directory : Path.Combine(directory, pinned.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root))
        {
            problems.Add($"{label}/{pinned.Path}: directory is missing.");
            return 0;
        }

        PinnedTree actual = ComputeTree(directory, pinned.Path, ownedFiles, covered);
        if (actual.FileCount != pinned.FileCount || !string.Equals(actual.Sha256, pinned.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"{label}/{pinned.Path}: {actual.FileCount} file(s) with digest {actual.Sha256}, the manifest says {pinned.FileCount} and {pinned.Sha256}. "
                + "Review `git diff --stat` of the directory, then copy the new values into the manifest.");
        }

        return actual.FileCount;
    }

    private static void CheckCoverage(
        string directory,
        string label,
        string complete,
        List<string> ownedFiles,
        HashSet<string> covered,
        List<string> problems)
    {
        string root = complete == "." ? directory : Path.Combine(directory, complete.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root))
        {
            problems.Add($"{label}: completeDirectories names '{complete}', which does not exist.");
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (IsNotesOrManifest(relative) || ownedFiles.Contains(relative, StringComparer.Ordinal) || covered.Contains(relative))
            {
                continue;
            }

            problems.Add($"{label}/{relative}: no entry in {ManifestFileName} covers this file. Pin it, or list it under ownedFiles if it is Heimdall's own.");
        }
    }

    /// <summary>
    /// Digest over the sorted <c>path TAB hash</c> lines of every file below
    /// <paramref name="treePath"/>, except the notes, the manifest and the owned files.
    /// </summary>
    private static PinnedTree ComputeTree(string directory, string treePath, List<string> ownedFiles, HashSet<string>? covered = null)
    {
        string root = treePath == "." ? directory : Path.Combine(directory, treePath.Replace('/', Path.DirectorySeparatorChar));
        List<(string Path, string Hash)> entries = [];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (IsNotesOrManifest(relative) || ownedFiles.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            entries.Add((relative, HashFile(file)));
            covered?.Add(relative);
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        StringBuilder lines = new();
        foreach ((string path, string hash) in entries)
        {
            lines.Append(path).Append('\t').Append(hash).Append('\n');
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
        return new PinnedTree { Path = treePath, FileCount = entries.Count, Sha256 = digest };
    }

    private static bool IsNotesOrManifest(string relative) =>
        string.Equals(relative, NotesFileName, StringComparison.Ordinal)
        || string.Equals(relative, ManifestFileName, StringComparison.Ordinal);

    /// <summary>SHA-256, lower-case hex; text files with CRLF folded to LF, binaries as they are.</summary>
    private static string HashFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (BinaryExtensions.Contains(Path.GetExtension(path)))
        {
            return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        }

        byte[] folded = new byte[data.Length];
        int length = 0;
        for (int index = 0; index < data.Length; index++)
        {
            if (data[index] == (byte)'\r' && index + 1 < data.Length && data[index + 1] == (byte)'\n')
            {
                continue;
            }

            folded[length++] = data[index];
        }

        return Convert.ToHexString(SHA256.HashData(folded.AsSpan(0, length))).ToLowerInvariant();
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, SolutionFile)))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing {SolutionFile} from test binary directory: {AppContext.BaseDirectory}");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "heimdall-vendored-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string content)
        {
            string full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory left behind in %TEMP% is not a test failure.
            }
        }
    }
}
