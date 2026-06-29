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
using System.Security.Cryptography;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Unit tests for <see cref="SshConnectionFactory"/>.
/// Focuses on auth method composition — the critical path for the
/// SSH password-auth bug (missing KeyboardInteractiveAuthenticationMethod).
/// </summary>
public sealed class SshConnectionFactoryTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the private <c>BuildAuthMethods</c> via reflection so we can
    /// assert on the composed auth method list without creating a real connection.
    /// </summary>
    private static List<AuthenticationMethod> InvokeBuildAuthMethods(SshConnectionParams connectionParams)
    {
        var method = typeof(SshConnectionFactory)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m =>
                m.Name == "BuildAuthMethods" &&
                m.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }] &&
                first == typeof(SshConnectionParams) &&
                second == typeof(SshAgentRegistry))
            ?? throw new InvalidOperationException("BuildAuthMethods not found — check method name or visibility.");

        var registry = new SshAgentRegistry(Array.Empty<ISshAgent>());
        var result = method.Invoke(null, [connectionParams, registry])
            ?? throw new InvalidOperationException("BuildAuthMethods returned null.");

        return (List<AuthenticationMethod>)result;
    }

    private static IEnumerable<string> MethodTypeNames(IEnumerable<AuthenticationMethod> methods)
        => methods.Select(m => m.GetType().Name);

    private static PrivateKeyFile GetSinglePrivateKeyFile(BaseClient client)
    {
        var method = Assert.Single(
            client.ConnectionInfo.AuthenticationMethods.OfType<PrivateKeyAuthenticationMethod>());
        var keyFile = Assert.Single(method.KeyFiles);
        return Assert.IsType<PrivateKeyFile>(keyFile);
    }

    private static bool IsPrivateKeyFileDisposed(PrivateKeyFile keyFile)
    {
        var field = typeof(PrivateKeyFile).GetField(
            "_isDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PrivateKeyFile._isDisposed field not found.");

        return Assert.IsType<bool>(field.GetValue(keyFile));
    }

    [Fact]
    public void HostKeyVerificationEndpoint_WithoutLogicalIdentity_UsesTransportEndpoint()
    {
        var connParams = new SshConnectionParams
        {
            Host = "127.0.0.1",
            Port = 42022,
            Username = "user"
        };

        Assert.Equal("127.0.0.1", connParams.HostKeyVerificationHost);
        Assert.Equal(42022, connParams.HostKeyVerificationPort);
    }

    [Fact]
    public void HostKeyVerificationEndpoint_WithLogicalIdentity_UsesLogicalEndpoint()
    {
        var connParams = new SshConnectionParams
        {
            Host = "127.0.0.1",
            Port = 42022,
            LogicalHost = "db.internal.example",
            LogicalPort = 22,
            Username = "user"
        };

        Assert.Equal("db.internal.example", connParams.HostKeyVerificationHost);
        Assert.Equal(22, connParams.HostKeyVerificationPort);
    }

    private static string CreateTempRsaPrivateKeyFile()
    {
        using var rsa = RSA.Create(2048);
        var keyBytes = rsa.ExportRSAPrivateKey();
        var pem = new string(PemEncoding.Write("RSA PRIVATE KEY", keyBytes));
        var path = Path.Combine(Path.GetTempPath(), $"heimdall_test_key_{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, pem);
        return path;
    }

    // ── BuildAuthMethods — password-only ──────────────────────────────

    [Fact]
    public void BuildAuthMethods_PasswordOnly_IncludesPasswordMethod()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret",
            KeyPath = null
        };

        var methods = InvokeBuildAuthMethods(connParams);

        Assert.Contains("PasswordAuthenticationMethod", MethodTypeNames(methods));
    }

    [Fact]
    public void BuildAuthMethods_PasswordOnly_IncludesKeyboardInteractiveMethod()
    {
        // Regression guard for SSH-01: servers with PasswordAuthentication no
        // require keyboard-interactive. Without this method, SSH.NET fails silently.
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret",
            KeyPath = null
        };

        var methods = InvokeBuildAuthMethods(connParams);

        Assert.Contains("KeyboardInteractiveAuthenticationMethod", MethodTypeNames(methods));
    }

    [Fact]
    public void BuildAuthMethods_PasswordOnly_PasswordComesBeforeKeyboardInteractive()
    {
        // SSH.NET tries methods in order. "password" should be attempted first
        // because it is faster (no server round-trip challenge). "keyboard-interactive"
        // is the fallback for servers that reject the "password" type.
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret",
            KeyPath = null
        };

        var methods = InvokeBuildAuthMethods(connParams);
        var names = MethodTypeNames(methods).ToList();

        var passwordIndex = names.IndexOf("PasswordAuthenticationMethod");
        var kbdIndex = names.IndexOf("KeyboardInteractiveAuthenticationMethod");

        Assert.True(passwordIndex >= 0, "PasswordAuthenticationMethod not found");
        Assert.True(kbdIndex >= 0, "KeyboardInteractiveAuthenticationMethod not found");
        Assert.True(passwordIndex < kbdIndex,
            "PasswordAuthenticationMethod must come before KeyboardInteractiveAuthenticationMethod");
    }

    // ── BuildAuthMethods — key file ───────────────────────────────────

    [Fact]
    public void BuildAuthMethods_WithKeyPath_UsesPrivateKeyMethod()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = keyPath
        };

        try
        {
            var methods = InvokeBuildAuthMethods(connParams);
            var names = MethodTypeNames(methods).ToList();

            Assert.Contains("PrivateKeyAuthenticationMethod", names);
            Assert.DoesNotContain("PasswordAuthenticationMethod", names);
            Assert.DoesNotContain("KeyboardInteractiveAuthenticationMethod", names);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void CreateSshClient_Dispose_DisposesPrivateKeyFile()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            KeyPath = keyPath
        };

        try
        {
            var client = SshConnectionFactory.CreateSshClient(
                connParams,
                new SshAgentRegistry(Array.Empty<ISshAgent>()));
            var keyFile = GetSinglePrivateKeyFile(client);

            Assert.False(IsPrivateKeyFileDisposed(keyFile));

            client.Dispose();

            Assert.True(IsPrivateKeyFileDisposed(keyFile));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void CreateSftpClient_Dispose_DisposesPrivateKeyFile()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            KeyPath = keyPath
        };

        try
        {
            var client = SshConnectionFactory.CreateSftpClient(
                connParams,
                new SshAgentRegistry(Array.Empty<ISshAgent>()));
            var keyFile = GetSinglePrivateKeyFile(client);

            Assert.False(IsPrivateKeyFileDisposed(keyFile));

            client.Dispose();

            Assert.True(IsPrivateKeyFileDisposed(keyFile));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildAuthMethods_KeyPathWithKeyPassphraseOnly_UsesPrivateKeyOnly()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPassphrase = "key-passphrase",
            KeyPath = keyPath
        };

        try
        {
            var methods = InvokeBuildAuthMethods(connParams);
            var names = MethodTypeNames(methods).ToList();

            Assert.Contains("PrivateKeyAuthenticationMethod", names);
            Assert.DoesNotContain("PasswordAuthenticationMethod", names);
            Assert.DoesNotContain("KeyboardInteractiveAuthenticationMethod", names);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildAuthMethods_KeyPathWithKeyPassphraseAndPassword_AddsPasswordFallback()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "login-password",
            KeyPassphrase = "key-passphrase",
            KeyPath = keyPath
        };

        try
        {
            var methods = InvokeBuildAuthMethods(connParams);
            var names = MethodTypeNames(methods).ToList();

            Assert.Contains("PrivateKeyAuthenticationMethod", names);
            Assert.Contains("PasswordAuthenticationMethod", names);
            Assert.Contains("KeyboardInteractiveAuthenticationMethod", names);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildAuthMethods_KeyPathWithPasswordWithoutLegacy_AddsPasswordFallback()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "login-password",
            KeyPassphrase = null,
            KeyPath = keyPath,
            UseLegacyPasswordAsKeyPassphrase = false
        };

        try
        {
            var methods = InvokeBuildAuthMethods(connParams);
            var names = MethodTypeNames(methods).ToList();

            Assert.Contains("PrivateKeyAuthenticationMethod", names);
            Assert.Contains("PasswordAuthenticationMethod", names);
            Assert.Contains("KeyboardInteractiveAuthenticationMethod", names);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildAuthMethods_LegacyKeyPathWithPassword_UsesPasswordAsPassphraseAndFallback()
    {
        var keyPath = CreateTempRsaPrivateKeyFile();
        var logDir = Path.Combine(Path.GetTempPath(), $"heimdall_ssh_log_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        FileLogger.Initialize(logDir, flushIntervalMs: 60000);
        FileLogger.SetEnabled(true);

        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "legacy-secret",
            KeyPassphrase = null,
            KeyPath = keyPath,
            UseLegacyPasswordAsKeyPassphrase = true,
            LegacyCredentialName = "legacy-profile"
        };

        try
        {
            var methods = InvokeBuildAuthMethods(connParams);
            FileLogger.Flush();
            var names = MethodTypeNames(methods).ToList();

            Assert.Contains("PrivateKeyAuthenticationMethod", names);
            Assert.Contains("PasswordAuthenticationMethod", names);
            Assert.Contains("KeyboardInteractiveAuthenticationMethod", names);

            var logFile = Directory.GetFiles(logDir, "heimdall_*.log").Single();
            var log = File.ReadAllText(logFile);
            Assert.Contains("Legacy credential mapping for server legacy-profile", log);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    // ── BuildAuthMethods — no credentials ────────────────────────────

    [Fact]
    public void BuildAuthMethods_NoCredentials_ContainsNoneMethod()
    {
        // When neither key nor password is set (and Pageant is unavailable, as on CI),
        // the fallback NoneAuthenticationMethod is added so SSH.NET has something to try.
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = null
        };

        var methods = InvokeBuildAuthMethods(connParams);

        // Either Pageant auth was added (if Pageant is running on dev machine)
        // or NoneAuthenticationMethod is the fallback. Either way, list is not empty.
        Assert.NotEmpty(methods);

        // If no Pageant on CI, only NoneAuthenticationMethod is present.
        // On dev machine with Pageant: PrivateKeyAuthenticationMethod (Pageant) is present.
        var names = MethodTypeNames(methods).ToList();
        var hasFallback = names.Contains("NoneAuthenticationMethod")
            || names.Contains("PrivateKeyAuthenticationMethod");

        Assert.True(hasFallback, "Expected either NoneAuthenticationMethod or Pageant key method.");
    }

    // ── Create — public API ───────────────────────────────────────────

    [Fact]
    public void Create_ReturnsConnectionInfo_WithCorrectHostAndPort()
    {
        var connParams = new SshConnectionParams
        {
            Host = "my-server.example.com",
            Port = 2222,
            Username = "admin",
            Password = "password123",
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        var info = SshConnectionFactory.Create(connParams);

        Assert.NotNull(info);
        Assert.Equal("my-server.example.com", info.Host);
        Assert.Equal(2222, info.Port);
        Assert.Equal("admin", info.Username);
    }

    [Fact]
    public void Create_ReturnsConnectionInfo_WithCorrectTimeout()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret",
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        var info = SshConnectionFactory.Create(connParams);

        Assert.Equal(TimeSpan.FromSeconds(30), info.Timeout);
    }

    [Fact]
    public void Create_NullParams_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SshConnectionFactory.Create(null!));
    }

    [Fact]
    public void Create_ReturnsConnectionInfo_WithAtLeastOneAuthMethod()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret"
        };

        var info = SshConnectionFactory.Create(connParams);

        Assert.NotEmpty(info.AuthenticationMethods);
    }

    [Fact]
    public void SshConnectionParams_DefaultKeepAliveInterval_UsesAppSettingsDefault()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user"
        };

        Assert.Equal(
            AppSettings.DefaultSshKeepAliveIntervalSeconds,
            connParams.KeepAliveIntervalSeconds);
    }

    [Fact]
    public void BuildAuthMethods_AvailableEmptyAgent_SkipsToNextAgentWithKeys()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = null
        };
        var emptyAgent = new FakeAgent("empty", available: true, []);
        var keyAgent = new FakeAgent(
            "keys",
            available: true,
            [new FakeAgentKey("ssh-ed25519", CreateAgentKeyBlob("ssh-ed25519"))]);
        var registry = new SshAgentRegistry([emptyAgent, keyAgent]);

        var method = typeof(SshConnectionFactory)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m =>
                m.Name == "BuildAuthMethods" &&
                m.GetParameters().Length == 2);
        var methods = (List<AuthenticationMethod>)method.Invoke(null, [connParams, registry])!;

        var names = MethodTypeNames(methods).ToList();
        Assert.Contains("PrivateKeyAuthenticationMethod", names);
        Assert.DoesNotContain("NoneAuthenticationMethod", names);
        Assert.Equal(1, emptyAgent.GetIdentitiesCallCount);
        Assert.Equal(1, keyAgent.GetIdentitiesCallCount);
    }

    // ── RequiresPlinkFallback ────────────────────────────────────────

    [Fact]
    public void RequiresPlinkFallback_NullParams_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => SshConnectionFactory.RequiresPlinkFallback(null!));
    }

    [Fact]
    public void RequiresPlinkFallback_WithPassword_NoAgentForwarding_ReturnsFalse()
    {
        // A connection without agent forwarding does not require Plink,
        // even if a compatible agent happens to be running.
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = "secret",
            KeyPath = null,
            AgentForwarding = false
        };

        var result = SshConnectionFactory.RequiresPlinkFallback(connParams);

        Assert.False(result, "Password auth should not require Plink fallback.");
    }

    [Fact]
    public void RequiresPlinkFallback_WithKeyPath_NoAgentForwarding_ReturnsFalse()
    {
        // A connection with an explicit key file does not require Plink fallback
        // (SSH.NET can handle the key directly).
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = @"C:\Users\user\.ssh\id_rsa",
            AgentForwarding = false
        };

        var result = SshConnectionFactory.RequiresPlinkFallback(connParams);

        Assert.False(result, "Agent forwarding is disabled, so Plink fallback is not required.");
    }

    [Fact]
    public void RequiresPlinkFallback_NoCredentials_NoAgentForwarding_ReturnsFalse()
    {
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Port = 22,
            Username = "user",
            Password = null,
            KeyPath = null,
            AgentForwarding = false
        };

        var result = SshConnectionFactory.RequiresPlinkFallback(connParams);
        Assert.False(result);
    }

    private static byte[] CreateAgentKeyBlob(string keyType)
    {
        var keyTypeBytes = System.Text.Encoding.ASCII.GetBytes(keyType);
        var blob = new byte[sizeof(uint) + keyTypeBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            blob.AsSpan(0, sizeof(uint)),
            (uint)keyTypeBytes.Length);
        keyTypeBytes.CopyTo(blob.AsSpan(sizeof(uint)));
        return blob;
    }

    private sealed class FakeAgent(
        string name,
        bool available,
        IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        public int GetIdentitiesCallCount { get; private set; }
        public string Name { get; } = name;
        public bool IsAvailable() => available;
        public IReadOnlyList<ISshAgentKey> GetIdentities()
        {
            GetIdentitiesCallCount++;
            return identities;
        }
    }

    private sealed class FakeAgentKey(string keyType, byte[] publicKeyBlob) : ISshAgentKey
    {
        public string Comment => "test-key";
        public string KeyType => keyType;
        public byte[] PublicKeyBlob => publicKeyBlob;
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1, 2, 3];
    }
}
