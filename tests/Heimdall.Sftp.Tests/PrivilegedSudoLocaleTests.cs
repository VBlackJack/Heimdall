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
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Every sudo the SFTP browser starts is started as <c>env LC_ALL=C sudo</c>, so sudo's own
/// messages are English whatever the locale of the server and the classifiers keep reading them.
/// </summary>
public sealed class PrivilegedSudoLocaleTests
{
    private const string MissingSudoStderr = "env: 'sudo': No such file or directory";

    [Fact]
    public void SudoProgram_IsExactlyEnvLcAllCSudo()
    {
        Assert.Equal("env LC_ALL=C sudo", PrivilegedFileCommands.SudoProgram);
    }

    [Fact]
    public void Builders_StartWithTheSudoProgram()
    {
        string prefix = PrivilegedFileCommands.SudoProgram + " ";

        Assert.StartsWith(prefix, PrivilegedFileCommands.BuildPasswordSudoInvocation("true"), StringComparison.Ordinal);
        Assert.StartsWith(prefix, PrivilegedFileCommands.BuildNonInteractiveSudoInvocation("true"), StringComparison.Ordinal);
    }

    [Fact]
    public void StagedOuterScript_ExportsTheCLocaleBeforeItsFirstSudo()
    {
        string outer = PrivilegedFileCommands.StagedWriteOuterScript;

        int export = outer.IndexOf("LC_ALL=C; export LC_ALL;", StringComparison.Ordinal);
        int firstSudo = outer.IndexOf("sudo", StringComparison.Ordinal);

        Assert.True(export >= 0);
        Assert.True(firstSudo >= 0);
        Assert.True(export < firstSudo);
    }

    [Theory]
    [InlineData(127, MissingSudoStderr, true)]
    [InlineData(127, "sh: 1: sudo: not found", true)]
    [InlineData(127, "env: 'other': No such file or directory", false)]
    [InlineData(1, MissingSudoStderr, false)]
    public void IsSudoMissing_RecognizesTheMessageOfEnv(int exitStatus, string stderr, bool expected)
    {
        Assert.Equal(expected, PrivilegedFileTransfer.IsSudoMissing(exitStatus, stderr));
    }

    [PosixShellTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeSudo_SeesTheCLocaleWhateverTheLoginLocale(bool passwordVariant)
    {
        string command = passwordVariant
            ? PrivilegedFileCommands.BuildPasswordSudoInvocation("true")
            : PrivilegedFileCommands.BuildNonInteractiveSudoInvocation("true");

        string directory = Path.Combine(Path.GetTempPath(), "heimdall-locale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "sudo"),
                "#!/bin/sh\nprintf 'LC_ALL=%s\nLANGUAGE=%s\n' \"$LC_ALL\" \"$LANGUAGE\" >> sudo-env.log\nexit 0\n",
                new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(directory, "sudo"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var start = new ProcessStartInfo(PosixShellLocator.Find()!)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                directory,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));
            start.Environment["LC_ALL"] = "fr_FR.UTF-8";
            start.Environment["LANG"] = "fr_FR.UTF-8";
            start.Environment["LANGUAGE"] = "fr";

            using Process process = Process.Start(start)!;
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);

            string logPath = Path.Combine(directory, "sudo-env.log");
            Assert.True(File.Exists(logPath), "The fake sudo did not run: " + await stderr);
            string log = File.ReadAllText(logPath);
            Assert.Contains("LC_ALL=C", log, StringComparison.Ordinal);
            Assert.DoesNotContain("fr_FR", log, StringComparison.Ordinal);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
