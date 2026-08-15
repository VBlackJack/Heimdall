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

using System.Reflection;
using System.Text.Json;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class FtpBrowserAtomicUploadTests
{
    private const string CombinedWarningKey = "WarnFtpReplacementNonAtomicMetadataNotPreserved";

    [Fact]
    public void CreateUploadTempRemotePath_KeepsSameRemoteDirectoryAndUsesSlashSeparators()
    {
        string tempPath = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");

        Assert.StartsWith("/srv/app/config.txt.", tempPath, StringComparison.Ordinal);
        Assert.EndsWith(".part", tempPath, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', tempPath);
        Assert.Equal("/srv/app", GetRemoteDirectory(tempPath));
    }

    [Fact]
    public void CreateUploadTempRemotePath_ReturnsDistinctPath()
    {
        string finalPath = "/srv/app/config.txt";
        string tempPath = FtpBrowser.CreateUploadTempRemotePath(finalPath);

        Assert.NotEqual(finalPath, tempPath);
    }

    [Fact]
    public void CreateUploadTempRemotePath_GeneratesUniquePaths()
    {
        string first = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");
        string second = FtpBrowser.CreateUploadTempRemotePath("/srv/app/config.txt");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Disconnect_WaitsForOperationLock()
    {
        using FtpBrowser browser = new();
        SemaphoreSlim opLock = GetOperationLock(browser);

        await opLock.WaitAsync();
        Task disconnectTask = Task.Run(browser.Disconnect);

        try
        {
            Task completed = await Task.WhenAny(disconnectTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(disconnectTask, completed);
        }
        finally
        {
            opLock.Release();
        }

        await disconnectTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CreateFtpExistingTargetReplaced_CarriesTheCombinedKeyAndTheFinalPath()
    {
        RemoteOperationWarning warning =
            RemoteOperationWarning.CreateFtpExistingTargetReplaced("/srv/app/config.txt");

        Assert.Equal(CombinedWarningKey, warning.WarningKey);
        Assert.Equal("/srv/app/config.txt", warning.RemotePath);

        // Not the same warning as the plain atomicity notice, which claims nothing about metadata
        // and stays in use for transports that do carry the replaced file's attributes across.
        Assert.NotEqual(
            RemoteOperationWarning.CreateNonAtomicReplacement("/srv/app/config.txt").WarningKey,
            warning.WarningKey);
    }

    [Fact]
    public void UploadFileAsync_WiresTheCombinedWarningIntoTheCommitRename()
    {
        string uploadMethod = ExtractMethod(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Heimdall.Sftp", "FtpBrowser.cs")),
            "public async Task UploadFileAsync(");

        // Scoped to the one method that performs the replacement: a document-wide scan would let
        // any other member of the file answer for this one.
        Assert.Contains(
            "onExistingTargetReplaced: () => OperationWarningRaised?.Invoke(",
            uploadMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteOperationWarning.CreateFtpExistingTargetReplaced(",
            uploadMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNonAtomicReplacement", uploadMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedWarning_StatesBothFactsInEnglishAndFrench()
    {
        string english = ReadLocaleValue("en.json", CombinedWarningKey);
        string french = ReadLocaleValue("fr.json", CombinedWarningKey);

        // One argument is formatted at the call site, the remote path, so both messages carry
        // exactly one placeholder and the same one.
        Assert.Equal(1, CountOccurrences(english, "{0}"));
        Assert.Equal(1, CountOccurrences(french, "{0}"));

        // Facet by facet, because both halves have to survive translation. A message reduced to the
        // atomicity half would quietly restore the silence about metadata that this warning exists
        // to break, and a whole-string comparison would not say which half went missing.
        AssertContainsAll(
            english,
            ["atomic", "owner", "permissions", "timestamps", "ACL", "extended attributes", "capabilities"]);
        // Escaped rather than literal so this file stays pure ASCII, exactly like the locale files.
        AssertContainsAll(
            french,
            [
                "atomique", "propri\u00e9taire", "permissions", "horodatages", "ACL",
                "attributs \u00e9tendus", "capabilities"
            ]);
    }

    private static void AssertContainsAll(string message, string[] fragments)
    {
        foreach (string fragment in fragments)
        {
            Assert.Contains(fragment, message, StringComparison.Ordinal);
        }
    }

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        for (int index = value.IndexOf(fragment, StringComparison.Ordinal);
             index >= 0;
             index = value.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadLocaleValue(string localeFileName, string key)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(FindRepoRoot(), "locales", localeFileName)));

        Assert.True(
            document.RootElement.TryGetProperty(key, out JsonElement value),
            $"Locale file '{localeFileName}' does not define '{key}'.");

        return Assert.IsType<string>(value.GetString());
    }

    private static string ExtractMethod(string source, string signature)
    {
        int methodStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Method signature was not found: {signature}");

        int openingBrace = source.IndexOf('{', methodStart);
        Assert.True(openingBrace >= 0, $"Method opening brace was not found: {signature}");

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
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
                    return source[methodStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method closing brace was not found: {signature}");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        int separator = remotePath.LastIndexOf('/');
        return separator <= 0 ? "/" : remotePath[..separator];
    }

    private static SemaphoreSlim GetOperationLock(FtpBrowser browser)
    {
        FieldInfo? field = typeof(FtpBrowser).GetField(
            "_opLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SemaphoreSlim>(field!.GetValue(browser));
    }
}
