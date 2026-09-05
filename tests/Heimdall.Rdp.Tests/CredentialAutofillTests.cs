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
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.Core.Logging;
using Heimdall.Rdp;

namespace Heimdall.Rdp.Tests;

[CollectionDefinition("CredentialAutofillLogging", DisableParallelization = true)]
public sealed class CredentialAutofillLoggingCollectionDefinition;

[Collection("CredentialAutofillLogging")]
public sealed class CredentialAutofillTests : IDisposable
{
    private readonly string _tempDir;

    public CredentialAutofillTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Rdp.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        FileLogger.Initialize(_tempDir, flushIntervalMs: 60000);
        FileLogger.SetEnabled(true);
    }

    public void Dispose()
    {
        FileLogger.Flush();
        FileLogger.SetEnabled(true);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData("Windows Security")]
    [InlineData("S\u00e9curit\u00e9 de Windows")]
    [InlineData("Securite Windows")]
    [InlineData("Windows-Sicherheit")]
    [InlineData("Seguridad de Windows")]
    [InlineData("Sicurezza di Windows")]
    [InlineData("Seguran\u00e7a do Windows")]
    [InlineData("Windows-beveiliging")]
    [InlineData("Zabezpieczenia systemu Windows")]
    [InlineData("Credential")]
    [InlineData("Credenziale")]
    [InlineData("Credencial")]
    [InlineData("Anmeldeinformation")]
    [InlineData("mstsc.exe")]
    public void TitlePattern_MatchesKnownCredentialDialogTitles(string title)
    {
        Assert.Matches(CredentialAutofill.TitlePattern, title);
    }

    [Theory]
    [InlineData("Notepad")]
    [InlineData("File Explorer")]
    [InlineData("")]
    public void TitlePattern_DoesNotMatchUnrelatedTitles(string title)
    {
        Assert.DoesNotMatch(CredentialAutofill.TitlePattern, title);
    }

    // The title pattern above recognises a dialog in eight locales; the confirmation button has
    // to be recognised in the same eight, or the UI Automation path falls through to "the first
    // enabled button", which on the Windows Security dialog can be the choices link.
    [Theory]
    [InlineData("OK")]
    [InlineData("&OK")]
    [InlineData("Sign in")]
    [InlineData("Connect")]
    [InlineData("Se connecter")]
    [InlineData("Connexion")]
    [InlineData("Anmelden")]
    [InlineData("Verbinden")]
    [InlineData("Iniciar sesi\u00f3n")]
    [InlineData("Conectar")]
    [InlineData("Aceptar")]
    [InlineData("Accedi")]
    [InlineData("Connetti")]
    [InlineData("Entrar")]
    [InlineData("Ligar")]
    [InlineData("Aanmelden")]
    [InlineData("Verbinding maken")]
    [InlineData("Zaloguj si\u0119")]
    [InlineData("Po\u0142\u0105cz")]
    public void OkButtonPattern_MatchesTheConfirmationOfEveryClaimedLocale(string label)
    {
        Assert.Matches(CredentialAutofill.OkButtonPattern, label);
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Annuler")]
    [InlineData("Abbrechen")]
    [InlineData("More choices")]
    [InlineData("Weitere Optionen")]
    [InlineData("")]
    public void OkButtonPattern_DoesNotMatchTheOtherButtons(string label)
    {
        Assert.DoesNotMatch(CredentialAutofill.OkButtonPattern, label);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("Mot de passe")]
    [InlineData("Kennwort")]
    [InlineData("Contrase\u00f1a")]
    [InlineData("Senha")]
    [InlineData("Wachtwoord")]
    [InlineData("Has\u0142o")]
    public void PasswordFieldPattern_MatchesTheFieldOfEveryClaimedLocale(string name)
    {
        Assert.Matches(CredentialAutofill.PasswordFieldPattern, name);
    }

    // One process handle per process for the life of the watcher, not one per visible window
    // per scan.
    [Fact]
    public void ResolveProcessName_ResolvesEachProcessOnce()
    {
        Dictionary<int, string> cache = new();
        int calls = 0;
        string Resolve(int pid)
        {
            calls++;
            return "process-" + pid;
        }

        Assert.Equal("process-7", CredentialAutofill.ResolveProcessName(7, cache, Resolve));
        Assert.Equal("process-7", CredentialAutofill.ResolveProcessName(7, cache, Resolve));
        Assert.Equal("process-9", CredentialAutofill.ResolveProcessName(9, cache, Resolve));

        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0x50000020L, true)]
    [InlineData(0x50000000L, false)]
    [InlineData(0L, false)]
    public void HasPasswordStyle_ReturnsExpectedResult(long style, bool expected)
    {
        bool actual = CredentialAutofill.HasPasswordStyle(style);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryAutofillConfirmedPasswordField_NoConfirmedField_FailsWithoutWritingOrSubmitting()
    {
        const string Secret = "must-not-be-written-or-logged";
        var fields = new List<CredentialAutofill.PasswordFieldCandidate<string>>
        {
            new("username", "User name", IsEnabled: true, IsPassword: false),
            new("disabled-password", "Password", IsEnabled: false, IsPassword: true)
        };
        var writeAttempts = 0;
        var submitAttempts = 0;

        bool result = CredentialAutofill.TryAutofillConfirmedPasswordField(
            fields,
            Secret,
            (_, _) =>
            {
                writeAttempts++;
                return true;
            },
            () =>
            {
                submitAttempts++;
                return true;
            });

        Assert.False(result);
        Assert.Equal(0, writeAttempts);
        Assert.Equal(0, submitAttempts);

        FileLogger.Flush();
        string[] logLines = ReadLogLines();
        Assert.Contains(logLines, line =>
            line.Contains(
                "CredentialAutofill abandoned because no confirmed password field was found.",
                StringComparison.Ordinal));
        Assert.DoesNotContain(logLines, line => line.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void SelectCredentialDialogTarget_ReturnsNull_ForUnmatchedBrokerWindows()
    {
        var windows = new List<CredentialAutofill.WindowInfo>
        {
            new(new IntPtr(0x1001), "Windows Security", "Credential Dialog Xaml Host", 2001, "CredentialUIBroker"),
            new(new IntPtr(0x1002), "Windows Security", "Credential Dialog Xaml Host", 2002, "CredentialUIBroker")
        };
        var hostHintPattern = new Regex("server01\\.corp\\.local", RegexOptions.IgnoreCase);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: 9999,
            hostHintPattern: hostHintPattern,
            candidates: windows,
            scan: 1);

        Assert.Null(result);
    }

    [Fact]
    public void SelectCredentialDialogTarget_NoMatch_LogsStructuredBrokerDiagnostics()
    {
        var windows = new List<CredentialAutofill.WindowInfo>
        {
            new(new IntPtr(0x1001), "Windows Security", "Credential Dialog Xaml Host", 2001, "CredentialUIBroker"),
            new(new IntPtr(0x1002), "Windows Security", "Credential Dialog Xaml Host", 2002, "CredentialUIBroker")
        };
        const string targetHost = "server01.corp.local";
        var hostHintPattern = new Regex(Regex.Escape(targetHost), RegexOptions.IgnoreCase);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: 9999,
            hostHintPattern: hostHintPattern,
            candidates: windows,
            scan: 3,
            targetHost: targetHost,
            visibleWindowCount: 12);

        Assert.Null(result);
        FileLogger.Flush();
        var lines = ReadLogLines();
        var debugLine = Assert.Single(lines, line =>
            line.Contains("[DEBUG]", StringComparison.Ordinal)
            && line.Contains("CredentialAutofillBrokerMatch ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(ExtractPayload(debugLine, "CredentialAutofillBrokerMatch "));
        var root = document.RootElement;

        Assert.Equal(targetHost, root.GetProperty("targetHost").GetString());
        Assert.Equal(12, root.GetProperty("visibleWindowCount").GetInt32());
        Assert.Equal(2, root.GetProperty("candidateCount").GetInt32());
        Assert.Equal("no-match", root.GetProperty("outcome").GetString());

        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, candidate =>
        {
            Assert.True(candidate.TryGetProperty("title", out _));
            Assert.True(candidate.TryGetProperty("processName", out _));
            Assert.True(candidate.TryGetProperty("decision", out _));
            Assert.True(candidate.TryGetProperty("reason", out _));
        });
        Assert.Contains(lines, line =>
            line.Contains("[INFO]", StringComparison.Ordinal)
            && line.Contains("CredentialAutofill broker match outcome", StringComparison.Ordinal));
    }

    private const int OwnProcessId = 777;
    private static readonly IntPtr SurfaceA = new(0xA000);
    private static readonly IntPtr SurfaceB = new(0xB000);

    private static List<CredentialAutofill.WindowInfo> TwoInProcessPrompts(IntPtr ownerOfFirst, IntPtr ownerOfSecond) =>
    [
        new(new IntPtr(0x3001), "Windows Security", "Credential Dialog Xaml Host", OwnProcessId, "Heimdall", ownerOfFirst),
        new(new IntPtr(0x3002), "Windows Security", "Credential Dialog Xaml Host", OwnProcessId, "Heimdall", ownerOfSecond)
    ];

    private static bool OwnerEquals(IntPtr owner, IntPtr surface) => owner == surface;

    // Two embedded sessions prompting at once raise two identical dialogs in this process. The
    // process match cannot tell them apart, and the first-in-list pick used to hand the second
    // session's prompt this session's password. The owner window is what says whose it is.
    [Fact]
    public void SelectCredentialDialogTarget_TwoInProcessPrompts_PicksTheOneOwnedByThisSurface()
    {
        var windows = TwoInProcessPrompts(SurfaceA, SurfaceB);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: OwnProcessId,
            hostHintPattern: null,
            candidates: windows,
            scan: 1,
            sessionSurfaceHandle: SurfaceB,
            isOwnedBySurface: OwnerEquals);

        Assert.NotNull(result);
        Assert.Equal(new IntPtr(0x3002), result.Value.Handle);
    }

    [Fact]
    public void SelectCredentialDialogTarget_PromptOwnedByAnotherSurface_IsLeftToItsOwnWatcher()
    {
        var windows = TwoInProcessPrompts(SurfaceA, SurfaceA);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: OwnProcessId,
            hostHintPattern: null,
            candidates: windows,
            scan: 1,
            sessionSurfaceHandle: SurfaceB,
            isOwnedBySurface: OwnerEquals);

        Assert.Null(result);
    }

    // Positive control for the fallback: when no prompt carries an owner at all, nothing is known
    // about ownership and the process match decides as it did before this existed.
    [Fact]
    public void SelectCredentialDialogTarget_NoPromptCarriesAnOwner_FallsBackToTheProcessMatch()
    {
        var windows = TwoInProcessPrompts(IntPtr.Zero, IntPtr.Zero);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: OwnProcessId,
            hostHintPattern: null,
            candidates: windows,
            scan: 1,
            sessionSurfaceHandle: SurfaceB,
            isOwnedBySurface: OwnerEquals);

        Assert.NotNull(result);
        Assert.Equal(new IntPtr(0x3001), result.Value.Handle);
    }

    [Fact]
    public void SelectCredentialDialogTarget_NoSurfaceNamed_IgnoresOwnership()
    {
        var windows = TwoInProcessPrompts(SurfaceA, SurfaceB);

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: OwnProcessId,
            hostHintPattern: null,
            candidates: windows,
            scan: 1,
            isOwnedBySurface: (_, _) => throw new InvalidOperationException("ownership must not be consulted"));

        Assert.NotNull(result);
    }

    [Fact]
    public void SelectCredentialDialogTarget_BrokerEnumerationException_LogsWarning()
    {
        var windows = new List<CredentialAutofill.WindowInfo>
        {
            new(new IntPtr(0x2001), "Windows Security", "Credential Dialog Xaml Host", 3001, string.Empty)
        };

        var result = CredentialAutofill.SelectCredentialDialogTarget(
            mstscProcessId: 9999,
            hostHintPattern: new Regex("server01", RegexOptions.IgnoreCase),
            candidates: windows,
            scan: 1,
            targetHost: "server01",
            visibleWindowCount: 1,
            isCredentialBroker: _ => throw new InvalidOperationException("broker enumeration failed"));

        Assert.Null(result);
        FileLogger.Flush();
        var warningLine = Assert.Single(ReadLogLines(), line =>
            line.Contains("[WARN]", StringComparison.Ordinal)
            && line.Contains("CredentialAutofillBrokerEnumerationFailed ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(ExtractPayload(
            warningLine,
            "CredentialAutofillBrokerEnumerationFailed "));

        Assert.Equal("InvalidOperationException", document.RootElement.GetProperty("exceptionType").GetString());
    }

    private string[] ReadLogLines()
    {
        var logFile = Directory.GetFiles(_tempDir, "heimdall_*.log").Single();
        return File.ReadAllLines(logFile);
    }

    private static string ExtractPayload(string line, string marker)
    {
        var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        return line[(markerIndex + marker.Length)..];
    }
}
