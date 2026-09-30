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
using System.Security.Cryptography;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Services.WinRm;

/// <summary>
/// Result of writing a transient WinRM credential bootstrap script.
/// </summary>
internal sealed record WinRmCredentialBootstrapResult(string ScriptPath);

/// <summary>
/// Writes the local PowerShell script that reconstructs a PSCredential and enters WinRM.
/// </summary>
internal sealed class WinRmCredentialBootstrap
{
    internal const string ScriptFilePrefix = "heimdall_winrm_";
    internal const string ScriptSearchPattern = "heimdall_winrm_*.ps1";

    private readonly Func<string> _createScriptPath;
    private readonly Action<string, string> _writeAndProtect;
    private readonly Func<string?, byte[]?> _unprotectStoredPasswordBytes;
    private readonly Func<byte[], string> _protectBootstrapPasswordBytes;

    public WinRmCredentialBootstrap(
        Func<string>? createScriptPath = null,
        Action<string, string>? writeAndProtect = null,
        Func<string?, byte[]?>? unprotectStoredPasswordBytes = null,
        Func<byte[], string>? protectBootstrapPasswordBytes = null)
    {
        _createScriptPath = createScriptPath ?? CreateDefaultScriptPath;
        _writeAndProtect = writeAndProtect ?? SecureFileWriter.WriteAndProtect;
        _unprotectStoredPasswordBytes = unprotectStoredPasswordBytes ?? CredentialProtector.UnprotectToBytes;
        _protectBootstrapPasswordBytes = protectBootstrapPasswordBytes ?? DpapiProvider.ProtectBytes;
    }

    public WinRmCredentialBootstrapResult Write(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Write(
            server,
            server.RemoteServer,
            WinRmPowerShellLaunchBuilder.ResolvePort(server));
    }

    public WinRmCredentialBootstrapResult Write(
        ServerProfileDto server,
        string computerName,
        int port)
    {
        ValidateCredentialProfile(server);

        byte[] plaintextBytes = UnprotectStoredPassword(server.WinRmPasswordEncrypted);

        try
        {
            string dpapiPasswordBlob = ProtectBootstrapPassword(plaintextBytes);
            string script = BuildScript(server, dpapiPasswordBlob, computerName, port);
            string scriptPath = _createScriptPath();

            _writeAndProtect(scriptPath, script);
            return new WinRmCredentialBootstrapResult(scriptPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    /// <summary>
    /// Reads the stored password, turning each way it can be unreadable into the message that
    /// names its cause: a locked vault asks for an unlock, anything else is an unavailable
    /// credential. Neither may read as a failure to launch the terminal.
    /// </summary>
    private byte[] UnprotectStoredPassword(string? protectedPassword)
    {
        byte[]? plaintextBytes;
        try
        {
            plaintextBytes = _unprotectStoredPasswordBytes(protectedPassword);
        }
        catch (VaultLockedException ex)
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmVaultLocked",
                [],
                "The vault is locked, so the WinRM stored credential cannot be read.",
                ex);
        }
        catch (CryptographicException ex)
        {
            throw CredentialUnavailable("WinRM stored credential could not be decrypted.", ex);
        }

        return plaintextBytes
            ?? throw CredentialUnavailable("WinRM stored credential could not be decrypted.");
    }

    private string ProtectBootstrapPassword(byte[] plaintextBytes)
    {
        try
        {
            return _protectBootstrapPasswordBytes(plaintextBytes);
        }
        catch (CryptographicException ex)
        {
            throw CredentialUnavailable("WinRM bootstrap credential could not be protected.", ex);
        }
    }

    private static WinRmConfigurationException CredentialUnavailable(
        string technicalDetail,
        Exception? innerException = null)
        => new("ErrorWinRmCredentialUnavailable", [], technicalDetail, innerException);

    public void Delete(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return;
        }

        try
        {
            File.Delete(scriptPath);
        }
        catch (IOException ex)
        {
            FileLogger.Warn($"[WinRmCredentialBootstrap] Delete bootstrap script failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLogger.Warn($"[WinRmCredentialBootstrap] Delete bootstrap script unauthorized: {ex.Message}");
        }
    }

    internal static string CreateDefaultScriptPath()
        => Path.Combine(Path.GetTempPath(), $"{ScriptFilePrefix}{Guid.NewGuid():N}.ps1");

    internal static string BuildScript(ServerProfileDto server, string dpapiPasswordBlob)
    {
        ArgumentNullException.ThrowIfNull(server);
        return BuildScript(
            server,
            dpapiPasswordBlob,
            server.RemoteServer,
            WinRmPowerShellLaunchBuilder.ResolvePort(server));
    }

    internal static string BuildScript(
        ServerProfileDto server,
        string dpapiPasswordBlob,
        string computerName,
        int port)
    {
        ValidateCredentialProfile(server);
        ArgumentException.ThrowIfNullOrEmpty(dpapiPasswordBlob);

        string usernameLiteral = WinRmPowerShellLaunchBuilder.QuotePowerShellLiteral(server.WinRmUsername!);
        string blobLiteral = WinRmPowerShellLaunchBuilder.QuotePowerShellLiteral(dpapiPasswordBlob);
        string enterCommand = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            computerName,
            port,
            "$credential");

        string[] lines =
        [
            "$ErrorActionPreference = 'Stop'",
            "$scriptPath = $PSCommandPath",
            "Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue",
            // ProtectedData lives in System.Security on .NET Framework / PS 5.1 and in System.Security.Cryptography.ProtectedData on .NET / PS 7; load whichever the host has, ignore the other.
            "try { Add-Type -AssemblyName System.Security -ErrorAction Stop } catch { }",
            "try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop } catch { }",
            "$blob = " + blobLiteral,
            "try {",
            "    $encryptedBytes = [Convert]::FromBase64String($blob)",
            "    $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($encryptedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)",
            "    $plainChars = [System.Text.Encoding]::UTF8.GetChars($plainBytes)",
            "    $securePassword = New-Object System.Security.SecureString",
            "    foreach ($ch in $plainChars) { $securePassword.AppendChar($ch) }",
            "    $securePassword.MakeReadOnly()",
            "    $credential = [System.Management.Automation.PSCredential]::new(" + usernameLiteral + ", $securePassword)",
            "    " + enterCommand,
            "    " + WinRmPowerShellLaunchBuilder.RemoteSessionEnteredAssignment,
            "}",
            "finally {",
            "    if ($encryptedBytes -ne $null) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }",
            "    if ($plainBytes -ne $null) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }",
            "    if ($plainChars -ne $null) { [Array]::Clear($plainChars, 0, $plainChars.Length) }",
            "    # Best-effort: $blob holds user-scoped DPAPI ciphertext, not plaintext; minimize heap residency.",
            "    $blob = $null",
            "    $securePassword = $null",
            "    $credential = $null",
            "}"
        ];

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Whether a username can go into the stored-credential bootstrap. The server dialog judges
    /// the field by this same predicate, so Save refuses exactly what a connection would.
    /// </summary>
    internal static bool IsValidUsername(string? username)
        => !string.IsNullOrWhiteSpace(username)
            && InputValidator.Validate(username, "Username");

    private static void ValidateCredentialProfile(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.WinRmIdentityMode != WinRmIdentityMode.Credential)
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmProfileInvalid",
                [],
                "WinRM bootstrap is only valid for stored-credential profiles.");
        }

        if (!IsValidUsername(server.WinRmUsername))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmInvalidUsername",
                [],
                "Invalid WinRM username.");
        }

        if (string.IsNullOrWhiteSpace(server.WinRmPasswordEncrypted))
        {
            throw new WinRmConfigurationException(
                "ErrorWinRmCredentialMissing",
                [],
                "WinRM stored credential is missing.");
        }

        WinRmPowerShellLaunchBuilder.ValidateProfile(server);
    }
}
