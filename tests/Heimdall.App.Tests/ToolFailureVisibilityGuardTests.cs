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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// A tool view may not answer a failed user gesture with a log line alone.
/// </summary>
/// <remarks>
/// <para>Nineteen catch blocks across eight views used to do exactly that. Someone
/// pressed Export, chose a filename, confirmed, and received no file and no message;
/// or pressed Copy and went on to paste whatever had been on the clipboard before;
/// or confirmed clearing the knowledge base and was left believing it had happened.
/// The evidence existed, in a log the user has no reason to know about.</para>
/// <para>This scans for the shape rather than the individual sites, so a view added
/// later inherits the rule. It deliberately does not prescribe which surface to use:
/// these views carry several, and unifying them would be an architecture decision
/// rather than a repair.</para>
/// </remarks>
public sealed class ToolFailureVisibilityGuardTests
{
    private const string ToolViewsRelativePath = "src/Heimdall.App/Views/Tools";

    /// <summary>
    /// Matches a catch block and captures its body up to the first line that closes
    /// at the same indentation as the <c>catch</c> keyword itself.
    /// </summary>
    private static readonly Regex CatchBlock = new(
        @"(?<indent>[ \t]*)catch \([^)]*\)\r?\n\k<indent>\{\r?\n(?<body>.*?)\r?\n\k<indent>\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:private|public|internal|protected)[^=;()]*\s(?<name>\w+)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoToolViewAnswersAUserGestureWithALogLineAlone()
    {
        List<string> offenders = [];

        foreach (string path in EnumerateToolViews())
        {
            string text = File.ReadAllText(path);

            foreach (Match match in CatchBlock.Matches(text))
            {
                string[] statements = match.Groups["body"].Value
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                    .ToArray();

                if (statements.Length == 0)
                {
                    // An empty catch is a different decision, deliberate or otherwise,
                    // and not what this guard is about.
                    continue;
                }

                bool logOnly = statements.All(line =>
                    line.Contains("FileLogger.", StringComparison.Ordinal));

                if (!logOnly)
                {
                    continue;
                }

                // Only a gesture the user made needs an answer. A message-pump or
                // parse guard that swallows malformed input is a different decision,
                // and turning it into a dialog would be a defect of its own.
                if (!AnswersAUserGesture(text, match.Index))
                {
                    continue;
                }

                int line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(path)}:{line}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These catch blocks report a failure to the log and to nobody else. "
            + "Add a message on the surface the view already owns:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  " + o)));
    }

    [Fact]
    public void TheScanActuallyReadsTheToolViews()
    {
        // Guarding the guard: a path typo or a moved directory would otherwise turn
        // the assertion above into a permanent, meaningless pass.
        string[] views = [.. EnumerateToolViews()];

        Assert.NotEmpty(views);
        Assert.Contains(views, v => Path.GetFileName(v) == "PortScannerView.xaml.cs");
    }

    [Fact]
    public void TheScanRecognisesALogOnlyCatch()
    {
        // And guarding it the other way: a regex that matched nothing would also pass.
        const string Sample = """
            void Example()
            {
                try
                {
                    Save();
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"failed: {ex.Message}");
                }
            }
            """;

        Match match = CatchBlock.Match(Sample.ReplaceLineEndings("\r\n"));

        Assert.True(match.Success);
        Assert.Contains("FileLogger.Warn", match.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the catch at <paramref name="offset"/> sits inside a method that runs
    /// because the user did something: a click, a request raised by a control, or an
    /// explicitly named export, copy or save.
    /// </summary>
    private static bool AnswersAUserGesture(string text, int offset)
    {
        string[] before = text[..offset].Split('\n');

        for (int i = before.Length - 1; i >= 0; i--)
        {
            Match declaration = MethodDeclaration.Match(before[i]);
            if (!declaration.Success)
            {
                continue;
            }

            string name = declaration.Groups["name"].Value;

            return name.EndsWith("Click", StringComparison.Ordinal)
                || name.EndsWith("Requested", StringComparison.Ordinal)
                || name.Contains("Export", StringComparison.Ordinal)
                || name.Contains("Copy", StringComparison.Ordinal)
                || name.Contains("Save", StringComparison.Ordinal);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateToolViews()
    {
        string dir = Path.Combine(
            FindRepoRoot(),
            ToolViewsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(dir), $"Tool views directory not found: {dir}");
        return Directory.EnumerateFiles(dir, "*.xaml.cs", SearchOption.TopDirectoryOnly);
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
