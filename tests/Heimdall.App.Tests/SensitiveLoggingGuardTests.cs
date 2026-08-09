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
    private const string EmbeddedRdpViewPath = @"src\Heimdall.App\Views\EmbeddedRdpView.xaml.cs";
    private const string RdpHandlerPath = @"src\Heimdall.App\Services\Handlers\RdpHandler.cs";

    private static readonly Regex LoggerCallRegex = new(
        @"(?s)(?:(?:Heimdall\.)?(?:Core\.Logging\.)?FileLogger\.(?:Info|Warn|Error)|_logInfo|_logWarning)\s*\((?<arguments>.*?)\);",
        RegexOptions.Compiled);

    private static readonly Regex RawStoreFrontUrlRegex = new(
        @"\b(?:validatedStoreFrontUrl|storeFrontUrl|storeFrontUri)\b|\bAbsoluteUri\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
}
