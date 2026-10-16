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
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// The global <see cref="FileLogger"/> has one instance, so the class that re-points it runs
/// alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedSessionPasswordLogCollection
{
    public const string Name = "Shared session password log file";
}

/// <summary>
/// Asking, remembering, reusing and forgetting a shared password writes no credential data to the
/// log: no login, no password, no sealed form, and not whether one was remembered.
/// </summary>
/// <remarks>
/// The password is sealed by a stand-in here, so the class reaches no process-global protector
/// state and can own the log file alone; the sealed form it produces is as recognisable in the log
/// as the real one. The session is direct for the same reason: the gateway check before a
/// connection opens a bastion's password with the protector itself. The bastion questions go
/// through the same code and write nothing either.
/// </remarks>
[Collection(SharedSessionPasswordLogCollection.Name)]
public sealed class SharedSessionPasswordLogTests : IDisposable
{
    private const string Login = "jdoe";
    private const string SessionPassword = "session-pw-8830";
    private const string SealPrefix = "stand-in-seal-";

    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"heimdall-shared-pw-log-{Guid.NewGuid():N}");

    public SharedSessionPasswordLogTests()
    {
        FileLogger.SetEnabled(true);
        FileLogger.Initialize(_logDirectory, flushIntervalMs: 60000);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private static ServerProfileDto Direct(ServerProfileDto profile)
    {
        ServerProfileDto direct = profile.CloneFaithfully();
        direct.SshGatewayId = null;
        direct.UseDirectConnection = true;
        return direct;
    }

    [Fact]
    public async Task AskRememberReuseAndForget_LogNoCredentialData()
    {
        await using SharedSessionConnectTests.Fixture fixture = await SharedSessionConnectTests.Fixture.CreateAsync(
            shared => new StubSharedSessionProvider([Direct(shared.Profile)], []),
            rememberSharedPasswords: true,
            seal: password => SealPrefix + new string(password.Reverse().ToArray()),
            isReadable: sealedSecret => sealedSecret.StartsWith(SealPrefix, StringComparison.Ordinal));
        fixture.Handler.Answer = SharedSessionPasswordConnectTests.SignedIn;

        // Asked and remembered, then reused, then rejected and forgotten, then asked again.
        fixture.Prompts!.Answers.Enqueue(new PasswordPromptResult(SessionPassword, Remember: true));
        await SharedSessionPasswordConnectTests.ConnectAsync(fixture);
        Assert.Equal(1, fixture.Secrets!.CountFor(SharedSessionTestData.SourceId));
        await SharedSessionPasswordConnectTests.ConnectAsync(fixture);
        fixture.Handler.Answer = () => SharedSessionPasswordConnectTests.Rejected(SshFailureCode.PasswordRejected);
        await SharedSessionPasswordConnectTests.ConnectAsync(fixture);
        Assert.Equal(0, fixture.Secrets.CountFor(SharedSessionTestData.SourceId));
        fixture.Prompts.Answers.Enqueue(new PasswordPromptResult(SessionPassword, Remember: false));
        await SharedSessionPasswordConnectTests.ConnectAsync(fixture);

        Assert.Equal(4, fixture.Handler.Dispatched.Count);
        Assert.Equal(2, fixture.Prompts.Prompts.Count);

        FileLogger.Flush();
        string log = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(_logDirectory, "heimdall_*.log").Select(File.ReadAllText));

        // The run did reach the log: an empty file would pass every assertion below.
        Assert.Contains(fixture.Shared.Profile.DisplayName, log, StringComparison.Ordinal);

        Assert.DoesNotContain(Login, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SessionPassword, log, StringComparison.Ordinal);
        Assert.DoesNotContain(SealPrefix, log, StringComparison.Ordinal);
        Assert.DoesNotContain("remember", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SharedSessionSecretStore.FileName, log, StringComparison.OrdinalIgnoreCase);
    }
}
