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
using Heimdall.App.Localization;
using Heimdall.App.Services.Handlers;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers how the native OpenSSH launch reports refusals and limits to the user.
/// </summary>
public sealed class OpenSshLaunchReportingTests
{
    private const string ConnectMethod = "private async Task<ConnectionResult> ConnectSshOpenSshAsync(";
    private const string RejectionMethod = "private ConnectionResult BuildOpenSshHostKeyRejectionResult(";

    [Fact]
    public void EveryLaunchFailureAndRefusalMapsToAKeyPresentInAllThreeLocales()
    {
        string[] keys =
        [
            .. Enum.GetValues<OpenSshLaunchFailure>().Select(OpenSshLaunchFailureMessages.KeyFor),
            .. Enum.GetValues<OpenSshRefusalReason>().Select(r => OpenSshLaunchFailureMessages.KeyFor(r)),
            SshLocalizationKeys.ErrorOpenSshHostKeyUnavailable,
            SshLocalizationKeys.StatusOpenSshEntriesNotApplied,
            SshLocalizationKeys.StatusOpenSshAcceptEnvOnly,
            SshLocalizationKeys.StatusOpenSshAgentKeyNotHonoured,
            SshLocalizationKeys.StatusOpenSshAcceptEnvAndAgentKey,
        ];

        foreach (string locale in new[] { "en", "fr", "es" })
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "locales", locale + ".json")));
            foreach (string key in keys.Distinct())
            {
                Assert.True(doc.RootElement.TryGetProperty(key, out JsonElement text), $"{key} missing in {locale}.json");
                Assert.False(string.IsNullOrWhiteSpace(text.GetString()), $"{key} is empty in {locale}.json");
            }
        }
    }

    [Fact]
    public void RefusalsMapToDistinctKeysWhereTheTextDiffers()
    {
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshNotFound, OpenSshLaunchFailureMessages.KeyFor(OpenSshRefusalReason.NotFound));
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshNotWindowsBuild, OpenSshLaunchFailureMessages.KeyFor(OpenSshRefusalReason.NotWindowsBuild));
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshVersionUnreadable, OpenSshLaunchFailureMessages.KeyFor(OpenSshRefusalReason.VersionUnreadable));
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshPathNotAllowed, OpenSshLaunchFailureMessages.KeyFor(OpenSshLaunchFailure.PathNotAllowed));
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshNoCommonKex, OpenSshLaunchFailureMessages.KeyFor(OpenSshLaunchFailure.NoCommonKexAlgorithm));
        Assert.Equal(SshLocalizationKeys.ErrorOpenSshSetEnvUnsupported, OpenSshLaunchFailureMessages.KeyFor(OpenSshLaunchFailure.SetEnvUnsupported));
    }

    [Fact]
    public void NativeNotices_NameTheAcceptEnvAndAgentKeyLimits_AndStaySilentOtherwise()
    {
        Assert.Null(SshCapabilityScope.Evaluate(SshResolvedPath.NativeOpenSsh, true, true, hasStartupCommandOrEnv: true));

        Assert.Equal(
            SshLocalizationKeys.StatusOpenSshAcceptEnvOnly,
            SshCapabilityScope.Evaluate(SshResolvedPath.NativeOpenSsh, false, false, sendsEnvironmentVariables: true)!.StatusLocalizationKey);
        Assert.Equal(
            SshLocalizationKeys.StatusOpenSshAgentKeyNotHonoured,
            SshCapabilityScope.Evaluate(SshResolvedPath.NativeOpenSsh, false, false, agentKeyChosen: true)!.StatusLocalizationKey);

        SshCapabilityNotice both = SshCapabilityScope.Evaluate(
            SshResolvedPath.NativeOpenSsh, false, false, sendsEnvironmentVariables: true, agentKeyChosen: true)!;
        Assert.Equal(SshLocalizationKeys.StatusOpenSshAcceptEnvAndAgentKey, both.StatusLocalizationKey);
        Assert.False(string.IsNullOrWhiteSpace(both.LogDescription));
    }

    [Fact]
    public void ConnectSshOpenSshAsync_KeepsNoneOfTheOldBehaviours()
    {
        string method = SliceFrom(ConnectMethod, null);

        // Absence only: the behaviours themselves are covered by the builder and mapping tests.
        Assert.DoesNotContain("ResolveExecutablePath", method, StringComparison.Ordinal);
        Assert.DoesNotContain("= server.SocksProxyPort", method.Replace("usesTunnel ? 0 : server.SocksProxyPort", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("= server.RemoteBindPort", method.Replace("usesTunnel ? 0 : server.RemoteBindPort", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("= server.RemoteLocalPort", method.Replace("usesTunnel ? 0 : server.RemoteLocalPort", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:0.0", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNativeHostKeyUnavailableCase_DoesNotUseThePlinkText()
    {
        string rejection = SliceFrom(RejectionMethod, ConnectMethod);

        Assert.DoesNotContain("SshLocalizationKeys.ErrorSshHostKeyUnavailable", rejection, StringComparison.Ordinal);
    }

    private static string SliceFrom(string startSignature, string? endSignature)
    {
        string source = ReadHandlerSource();
        int start = source.IndexOf(startSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, "Method not found in SshHandler.cs.");
        int end = endSignature is null ? source.Length : source.IndexOf(endSignature, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string ReadHandlerSource()
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "Services", "Handlers", "SshHandler.cs"));

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

        throw new DirectoryNotFoundException("Cannot find the repository root containing Heimdall.slnx.");
    }
}
