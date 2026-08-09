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
/// Locks the measured credential-log corrections and prevents raw StoreFront URLs from reaching
/// Citrix log calls while preserving the explicitly accepted failure diagnostics.
/// </summary>
public sealed class SensitiveLoggingGuardTests
{
    private const string ActiveXHostPath = @"src\Heimdall.Rdp\ActiveX\RdpActiveXHost.cs";
    private const string CitrixHandlerPath = @"src\Heimdall.App\Services\Handlers\CitrixHandler.cs";
    private const string CredentialManagerHelperPath = @"src\Heimdall.Rdp\CredentialManagerHelper.cs";
    private const string EmbeddedRdpViewPath = @"src\Heimdall.App\Views\EmbeddedRdpView.xaml.cs";
    private const string RdpHandlerPath = @"src\Heimdall.App\Services\Handlers\RdpHandler.cs";

    // Regime B classification: CredentialAutofill.cs performs CredUI broker enumeration under the
    // second credential-logging clause in CLAUDE.md:361. It is excluded by regime, not allowlisted.
    private const string CredentialAutofillRegimeBPath = @"src\Heimdall.Rdp\CredentialAutofill.cs";

    // Regime A is the credential connect path defined by the first clause in CLAUDE.md:361.
    private static readonly string[] CredentialRegimeAPaths =
    {
        ActiveXHostPath,
        CredentialManagerHelperPath,
        RdpHandlerPath,
        CitrixHandlerPath,
        EmbeddedRdpViewPath,
    };

    private static readonly string[] CredentialVocabulary =
    {
        "credential",
        "password",
        "cred",
        "vault",
        "logon",
        "autofill",
        "secret",
        "token",
    };

    private static readonly Regex LoggerCallRegex = new(
        @"(?s)(?:(?:Heimdall\.)?(?:Core\.Logging\.)?FileLogger\.(?:Info|Warn|Error)|_logInfo|_logWarning)\s*\((?<arguments>.*?)\);",
        RegexOptions.Compiled);

    private static readonly Regex RawStoreFrontUrlRegex = new(
        @"\b(?:validatedStoreFrontUrl|storeFrontUrl|storeFrontUri)\b|\bAbsoluteUri\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NominalCredentialLoggerStartRegex = new(
        @"(?<![\w.])(?:(?:Heimdall\.)?(?:Core\.Logging\.)?FileLogger\.(?:Info|Debug)|_logInfo)\s*\(",
        RegexOptions.Compiled);

    public static TheoryData<string, string> ClosedPresenceEmissions => new()
    {
        { ActiveXHostPath, "RdpActiveXHost.SetCredentials: valuesReceived=True" },
        { ActiveXHostPath, "RdpActiveXHost.SetClearTextPassword: success=True" },
        { ActiveXHostPath, "RdpActiveXHost.ApplyCredentialSettings: passwordInjected=" },
        { RdpHandlerPath, "RDP credentials stored for" },
        { RdpHandlerPath, "RDP credential injection skipped for" },
        { RdpHandlerPath, "RDP CredMan entry cleaned:" },
        { RdpHandlerPath, "RDP CredMan cleanup skipped for" },
        { EmbeddedRdpViewPath, "EmbeddedRDP SetCredentials called." },
    };

    public static TheoryData<string, string> PermittedFailureDiagnostics => new()
    {
        { ActiveXHostPath, "RdpActiveXHost.SetClearTextPassword: success=False" },
        { RdpHandlerPath, "Failed to store RDP credentials:" },
        { RdpHandlerPath, "RDP CredMan cleanup failed for" },
        { EmbeddedRdpViewPath, "Embedded RDP ClearPassword failed:" },
        { EmbeddedRdpViewPath, "EmbeddedRDP ClearPassword (login):" },
        { EmbeddedRdpViewPath, "Embedded RDP credential autofill failed:" },
    };

    [Fact]
    public void CitrixLogs_DoNotReceiveRawStoreFrontUrls()
    {
        string repoRoot = FindRepoRoot();
        string handlersDirectory = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Services",
            "Handlers");
        string[] citrixHandlers = SourceFileEnumeration
            .EnumerateFiles(handlersDirectory, "CitrixHandler.cs")
            .ToArray();
        string citrixHandler = Assert.Single(citrixHandlers);
        string source = File.ReadAllText(citrixHandler);
        List<string> violations = new();

        foreach (Match loggerCall in LoggerCallRegex.Matches(source))
        {
            Match rawUrl = RawStoreFrontUrlRegex.Match(loggerCall.Groups["arguments"].Value);
            if (!rawUrl.Success)
            {
                continue;
            }

            int line = GetLineNumber(source, loggerCall.Index);
            string relativePath = Path.GetRelativePath(repoRoot, citrixHandler);
            violations.Add($"  {relativePath}:{line} - {rawUrl.Value}");
        }

        Assert.True(
            violations.Count == 0,
            "Raw StoreFront URL expressions reached Citrix log calls:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RegimeAInfoAndDebugLogs_DoNotCarryCredentialVocabulary()
    {
        Assert.DoesNotContain(CredentialAutofillRegimeBPath, CredentialRegimeAPaths);

        List<CredentialLogViolation> violations = new();
        int inspectedCallSites = 0;

        foreach (string relativePath in CredentialRegimeAPaths)
        {
            CredentialLogScanResult result = ScanCredentialLogs(relativePath, ReadSource(relativePath));
            inspectedCallSites += result.InspectedCallSites;
            violations.AddRange(result.Violations);
        }

        Assert.True(
            violations.Count == 0,
            "Credential vocabulary reached Info/Debug log calls in regime A:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                violations.Select(violation =>
                    $"  {violation.RelativePath}:{violation.Line} - {violation.Vocabulary}"))
            + Environment.NewLine
            + $"Inspected call sites: {inspectedCallSites}");
    }

    [Fact]
    public void RegimeACredentialLogScanner_ReportsMultilineMutationWithFileAndLine()
    {
        const string fixturePath = @"fixtures\CredentialMutation.cs";
        const string source = """
            namespace Fixture;

            internal static class CredentialMutation
            {
                public static void Emit(string token)
                {
                    Core.Logging.FileLogger.Info(
                        $"Credential autofill state changed: token={token}");
                }
            }
            """;

        CredentialLogScanResult result = ScanCredentialLogs(fixturePath, source);
        CredentialLogViolation violation = Assert.Single(result.Violations);

        Assert.Equal(1, result.InspectedCallSites);
        Assert.Equal(fixturePath, violation.RelativePath);
        Assert.Equal(7, violation.Line);
        Assert.Equal("credential", violation.Vocabulary);
    }

    [Theory]
    [MemberData(nameof(ClosedPresenceEmissions))]
    public void ClosedRdpPresenceEmission_RemainsAbsent(string relativePath, string forbiddenFragment)
    {
        string source = ReadSource(relativePath);

        Assert.DoesNotContain(forbiddenFragment, source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PermittedFailureDiagnostics))]
    public void PermittedRdpFailureDiagnostic_RemainsWarn(string relativePath, string requiredFragment)
    {
        string source = ReadSource(relativePath);
        bool hasRequiredWarning = LoggerCallRegex.Matches(source)
            .Cast<Match>()
            .Any(match =>
                match.Value.Contains("FileLogger.Warn", StringComparison.Ordinal)
                && match.Groups["arguments"].Value.Contains(requiredFragment, StringComparison.Ordinal));

        Assert.True(
            hasRequiredWarning,
            $"Required RDP failure diagnostic is missing or no longer Warn: {relativePath} - {requiredFragment}");
    }

    private static int GetLineNumber(string source, int index)
    {
        return source.AsSpan(0, index).Count('\n') + 1;
    }

    private static CredentialLogScanResult ScanCredentialLogs(string relativePath, string source)
    {
        List<CredentialLogViolation> violations = new();
        int inspectedCallSites = 0;
        int searchIndex = 0;

        while (searchIndex < source.Length)
        {
            Match loggerStart = NominalCredentialLoggerStartRegex.Match(source, searchIndex);
            if (!loggerStart.Success)
            {
                break;
            }

            int openParenthesisIndex = loggerStart.Index + loggerStart.Value.LastIndexOf('(');
            int closeParenthesisIndex = FindMatchingCloseParenthesis(source, openParenthesisIndex);
            inspectedCallSites++;

            string loggerCall = source[loggerStart.Index..(closeParenthesisIndex + 1)];
            string? matchedVocabulary = CredentialVocabulary.FirstOrDefault(vocabulary =>
                loggerCall.Contains(vocabulary, StringComparison.OrdinalIgnoreCase));
            if (matchedVocabulary is not null)
            {
                violations.Add(new CredentialLogViolation(
                    relativePath,
                    GetLineNumber(source, loggerStart.Index),
                    matchedVocabulary));
            }

            searchIndex = closeParenthesisIndex + 1;
        }

        return new CredentialLogScanResult(inspectedCallSites, violations);
    }

    private static int FindMatchingCloseParenthesis(string source, int openParenthesisIndex)
    {
        int depth = 0;
        SourceLexicalState state = SourceLexicalState.Code;
        int rawStringDelimiterLength = 0;

        for (int index = openParenthesisIndex; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            switch (state)
            {
                case SourceLexicalState.SingleLineComment:
                    if (current == '\n')
                    {
                        state = SourceLexicalState.Code;
                    }

                    continue;

                case SourceLexicalState.MultiLineComment:
                    if (current == '*' && next == '/')
                    {
                        state = SourceLexicalState.Code;
                        index++;
                    }

                    continue;

                case SourceLexicalState.RegularString:
                    if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '"')
                    {
                        state = SourceLexicalState.Code;
                    }

                    continue;

                case SourceLexicalState.VerbatimString:
                    if (current != '"')
                    {
                        continue;
                    }

                    if (next == '"')
                    {
                        index++;
                    }
                    else
                    {
                        state = SourceLexicalState.Code;
                    }

                    continue;

                case SourceLexicalState.RawString:
                    if (current == '"'
                        && CountConsecutiveCharacters(source, index, '"') >= rawStringDelimiterLength)
                    {
                        index += rawStringDelimiterLength - 1;
                        state = SourceLexicalState.Code;
                    }

                    continue;

                case SourceLexicalState.Character:
                    if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = SourceLexicalState.Code;
                    }

                    continue;

                case SourceLexicalState.Code:
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported lexical state: {state}");
            }

            if (current == '/' && next == '/')
            {
                state = SourceLexicalState.SingleLineComment;
                index++;
            }
            else if (current == '/' && next == '*')
            {
                state = SourceLexicalState.MultiLineComment;
                index++;
            }
            else if (current == '"')
            {
                int quoteCount = CountConsecutiveCharacters(source, index, '"');
                if (quoteCount >= 3)
                {
                    rawStringDelimiterLength = quoteCount;
                    state = SourceLexicalState.RawString;
                    index += quoteCount - 1;
                }
                else
                {
                    state = IsVerbatimStringStart(source, index)
                        ? SourceLexicalState.VerbatimString
                        : SourceLexicalState.RegularString;
                }
            }
            else if (current == '\'')
            {
                state = SourceLexicalState.Character;
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw new InvalidDataException(
            $"Unbalanced logger call starting at line {GetLineNumber(source, openParenthesisIndex)}.");
    }

    private static int CountConsecutiveCharacters(string source, int startIndex, char character)
    {
        int count = 0;
        while (startIndex + count < source.Length && source[startIndex + count] == character)
        {
            count++;
        }

        return count;
    }

    private static bool IsVerbatimStringStart(string source, int quoteIndex)
    {
        return quoteIndex > 0 && source[quoteIndex - 1] == '@'
            || quoteIndex > 1 && source[quoteIndex - 1] == '$' && source[quoteIndex - 2] == '@';
    }

    private static string ReadSource(string relativePath)
    {
        string path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }

    private sealed record CredentialLogScanResult(
        int InspectedCallSites,
        IReadOnlyList<CredentialLogViolation> Violations);

    private sealed record CredentialLogViolation(string RelativePath, int Line, string Vocabulary);

    private enum SourceLexicalState
    {
        Code,
        SingleLineComment,
        MultiLineComment,
        RegularString,
        VerbatimString,
        RawString,
        Character,
    }
}
