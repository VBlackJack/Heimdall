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
using Heimdall.App.Tests.Views.EmbeddedRdp;

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
    private const string RdpPasswordResetPath = @"src\Heimdall.Rdp\ActiveX\RdpPasswordReset.cs";

    private const string DisconnectedCallback = "private void OnRdpDisconnected(int reason)";
    private const string FatalErrorCallback = "private void OnRdpFatalError(int errorCode)";

    // The three steps whose order the disconnect callback owes, each carried whole. The reset is
    // the one that clears the credential the control holds; a three-way ordering of IndexOf
    // results is satisfied by the same reset folded behind a term that is false by construction,
    // which leaves the text at the same offset, the ordering intact, and this guard reporting a
    // secret cleared that is not.
    private const string DisposedGuard = "if (_disposed)";
    private const string NativePasswordReset =
        "TryResetNativePassword(nameof(OnRdpDisconnected));";
    private const string WatchdogGuard = "if (_connectAttempts.AbandonedByWatchdog)";

    // The same statement on the fatal-error path, carried whole for the same reason. This is the
    // twin of the reset above: it was left as a bare IndexOf ordering over raw source when its
    // sibling twenty lines up was repaired, so the wipe on the fatal path could be folded behind a
    // term that is false by construction and this file would still report the secret cleared.
    private const string FatalNativePasswordReset =
        "TryResetNativePassword(nameof(OnRdpFatalError));";

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
        RdpPasswordResetPath,
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

        // RDP-012: both historical ClearPassword diagnostics were emitted from connected-state call
        // sites where put_ClearTextPassword always fails, so the effacement never happened. The
        // calls and their diagnostics are removed; their absence is now the contract.
        { EmbeddedRdpViewPath, "Embedded RDP ClearPassword failed:" },
        { EmbeddedRdpViewPath, "EmbeddedRDP ClearPassword (login):" },
    };

    public static TheoryData<string, string> PermittedFailureDiagnostics => new()
    {
        { ActiveXHostPath, "RdpActiveXHost.SetClearTextPassword: success=False" },
        { RdpHandlerPath, "Failed to store RDP credentials:" },
        { RdpHandlerPath, "RDP CredMan cleanup failed for" },
        { EmbeddedRdpViewPath, "Embedded RDP credential autofill failed:" },
        { EmbeddedRdpViewPath, "EmbeddedRDP password reset not applied:" },
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

    [Fact]
    public void RdpConnectedCallbacks_ContainNoNativePasswordClearing()
    {
        string source = ReadSource(EmbeddedRdpViewPath);
        string[] connectedCallbackSignatures =
        {
            "private void OnRdpConnected()",
            "private void OnRdpLoginComplete()",
        };

        foreach (string signature in connectedCallbackSignatures)
        {
            string body = ExtractMethodBody(source, signature);

            Assert.DoesNotContain("ClearPassword", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ResetPassword", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TryResetNativePassword", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RdpDisconnectedCallback_ResetsPasswordBetweenDisposedAndWatchdogGuards()
    {
        string body = DisconnectedCallbackLogic(ReadSource(EmbeddedRdpViewPath));

        // Read as a step of the callback before anything is read as an offset in it: the
        // ordering below holds just as well for a reset that is written and never runs.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(body, NativePasswordReset),
            "The native password reset is no longer a statement of OnRdpDisconnected: it is "
                + "absent, or it now sits inside a condition, or an unconditional return stands "
                + "above it. The credential the control holds is then not wiped when the session "
                + "drops, and the ordering asserted here would not notice.");

        int disposedGuardIndex = body.IndexOf(DisposedGuard, StringComparison.Ordinal);
        int resetIndex = body.IndexOf(NativePasswordReset, StringComparison.Ordinal);
        int watchdogGuardIndex = body.IndexOf(WatchdogGuard, StringComparison.Ordinal);

        Assert.True(disposedGuardIndex >= 0, "OnRdpDisconnected no longer guards on _disposed.");
        Assert.True(
            watchdogGuardIndex >= 0,
            "OnRdpDisconnected no longer returns early on a watchdog abort.");
        Assert.True(
            resetIndex > disposedGuardIndex,
            "The native password reset must run after the _disposed guard.");
        Assert.True(
            watchdogGuardIndex > resetIndex,
            "The native password reset must run before the watchdog early return.");
    }

    /// <summary>
    /// The control: the reading above rejects the reset kept exactly where it stands and folded
    /// behind a term that is false on every disconnect that reaches it.
    /// </summary>
    /// <remarks>
    /// Without it the assertion above is a presence with nothing proving that a reset which
    /// never fires can be observed. The mutant is built from the view's real source and its
    /// replacement count is asserted, so a mutant that failed to land cannot be read as a
    /// rejection of unmutated code.
    /// </remarks>
    [Fact]
    public void RdpDisconnectedPasswordReading_RejectsAResetFoldedBehindAnotherTerm()
    {
        string source = ReadSource(EmbeddedRdpViewPath);
        int occurrences = Regex.Matches(source, Regex.Escape(NativePasswordReset)).Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one '{NativePasswordReset}' in the view, found {occurrences}. A "
                + "mutant built from this would not measure what this test claims.");

        string mutated = source.Replace(
            NativePasswordReset,
            "if (_disposed) " + NativePasswordReset,
            StringComparison.Ordinal);
        Assert.NotEqual(source, mutated);

        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                DisconnectedCallbackLogic(mutated), NativePasswordReset),
            "A reset trailing a braceless condition satisfies this file's reading of the "
                + "callback, so the guard cannot tell a credential that is wiped from one that "
                + "is only written about.");
    }

    /// <summary>
    /// The disconnect callback of any version of the view, blanked of comments and literals.
    /// </summary>
    /// <remarks>
    /// Blanked first because a call left behind in a comment satisfies a substring search as
    /// readily as one that runs, and this callback is the guard that says a credential is gone.
    /// </remarks>
    private static string DisconnectedCallbackLogic(string source) => ExtractMethodBody(
        ViewSource.WithoutCommentsAndLiterals(source), DisconnectedCallback);

    [Fact]
    public void RdpFatalErrorCallback_ResetsPasswordAfterDisposedGuard()
    {
        string body = FatalErrorCallbackLogic(ReadSource(EmbeddedRdpViewPath));

        // Read as a step of the callback before anything is read as an offset in it, exactly as
        // the disconnect twin above does: the ordering below holds just as well for a reset that
        // is written and never runs.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(body, FatalNativePasswordReset),
            "The native password reset is no longer a statement of OnRdpFatalError: it is "
                + "absent, or it now sits inside a condition, or an unconditional return stands "
                + "above it. The credential the control holds is then not wiped when the session "
                + "fails, and the ordering asserted here would not notice.");

        int disposedGuardIndex = body.IndexOf(DisposedGuard, StringComparison.Ordinal);
        int resetIndex = body.IndexOf(FatalNativePasswordReset, StringComparison.Ordinal);

        Assert.True(disposedGuardIndex >= 0, "OnRdpFatalError no longer guards on _disposed.");
        Assert.True(
            resetIndex > disposedGuardIndex,
            "The native password reset must run after the _disposed guard.");
    }

    /// <summary>
    /// The control for the fatal-error path: the reading above rejects the reset kept exactly
    /// where it stands and folded behind a term that is false on every fatal error reaching it.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="RdpDisconnectedPasswordReading_RejectsAResetFoldedBehindAnotherTerm"/>,
    /// written at the same time as the reading it controls rather than a round later.
    /// </remarks>
    [Fact]
    public void RdpFatalErrorPasswordReading_RejectsAResetFoldedBehindAnotherTerm()
    {
        string source = ReadSource(EmbeddedRdpViewPath);
        int occurrences = Regex.Matches(source, Regex.Escape(FatalNativePasswordReset)).Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one '{FatalNativePasswordReset}' in the view, found "
                + $"{occurrences}. A mutant built from this would not measure what this test "
                + "claims.");

        string mutated = source.Replace(
            FatalNativePasswordReset,
            "if (_disposed) " + FatalNativePasswordReset,
            StringComparison.Ordinal);
        Assert.NotEqual(source, mutated);

        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                FatalErrorCallbackLogic(mutated), FatalNativePasswordReset),
            "A reset trailing a braceless condition satisfies this file's reading of the fatal "
                + "error callback, so the guard cannot tell a credential that is wiped from one "
                + "that is only written about.");
    }

    /// <summary>
    /// The fatal-error callback of any version of the view, blanked of comments and literals.
    /// </summary>
    private static string FatalErrorCallbackLogic(string source) => ExtractMethodBody(
        ViewSource.WithoutCommentsAndLiterals(source), FatalErrorCallback);

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        int openingBraceIndex = source.IndexOf('{', signatureIndex);
        Assert.True(openingBraceIndex >= 0, $"Method opening brace was not found: {signature}");

        int depth = 0;
        for (int index = openingBraceIndex; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openingBraceIndex..(index + 1)];
                }
            }
        }

        throw new InvalidDataException($"Unbalanced method body for signature: {signature}");
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
