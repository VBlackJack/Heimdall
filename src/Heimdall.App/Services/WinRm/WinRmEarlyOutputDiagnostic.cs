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

using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.App.Services.WinRm;

internal sealed class WinRmEarlyOutputDiagnostic
{
    internal const int DefaultMaxBufferedBytes = 16 * 1024;

    private const int DiagnosticContextRadius = 512;

    private const string NtlmLoopbackCode = "0x8009030e";
    private const string WsManInvalidResponseCode = "12152";

    private const string TrustedHostsSettingName = "TrustedHosts";
    private const string TrustedHostsErrorCode = "0x803381a1";
    private const string WrongPrincipalCode = "0x80090322";
    private const string AccessDeniedCode = "0x80070005";
    private const string LogonFailureCode = "0x8009030c";
    private const string BadCredentialsCode = "0x8007052e";

    /// <summary>
    /// Untranslated tokens of authentication failures and the locale key that explains each.
    /// </summary>
    /// <remarks>
    /// Order matters. A refused credential is tested first: the Negotiate logon failure lists
    /// TrustedHosts among its possible remedies, and a mistyped password must not be reported as
    /// a TrustedHosts problem. The TrustedHosts tokens come next, before the Kerberos principal
    /// code, because a TrustedHosts refusal also mentions Kerberos.
    /// </remarks>
    private static readonly (string Token, string Key)[] AuthenticationDiagnosticTokens =
    [
        (LogonFailureCode, "ErrorWinRmLogonFailed"),
        (BadCredentialsCode, "ErrorWinRmLogonFailed"),
        (AccessDeniedCode, "ErrorWinRmAccessDenied"),
        (TrustedHostsSettingName, "ErrorWinRmTrustedHosts"),
        (TrustedHostsErrorCode, "ErrorWinRmTrustedHosts"),
        (WrongPrincipalCode, "ErrorWinRmKerberosPrincipal")
    ];

    /// <summary>
    /// Help topic every execution-policy refusal names, untranslated in every host language.
    /// </summary>
    private const string ExecutionPolicyHelpTopic = "about_Execution_Policies";

    /// <summary>
    /// The prompt of an entered remote session, "[host]: PS path>". The colon may be preceded
    /// by a space, as localized hosts write it (measured on a French host: "[Processus :id] : PS").
    /// A bare local "PS path>" is deliberately not a match: it is what the host shows after
    /// Enter-PSSession failed, right below the error this diagnostic exists to explain.
    /// </summary>
    private static readonly Regex RemotePromptPattern = new(
        @"\[[^\]\r\n]+\] ?: ?PS [^\r\n]*>",
        RegexOptions.CultureInvariant);

    private readonly int _maxBufferedBytes;
    private readonly StringBuilder _buffer = new();
    private int _bufferedBytes;

    public WinRmEarlyOutputDiagnostic(int maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        if (maxBufferedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
        }

        _maxBufferedBytes = maxBufferedBytes;
        IsActive = true;
    }

    public bool IsActive { get; private set; }

    public void MarkUserInput()
    {
        IsActive = false;
    }

    public string? Observe(ReadOnlySpan<byte> data)
    {
        if (!IsActive || data.IsEmpty)
        {
            return null;
        }

        int remainingBytes = _maxBufferedBytes - _bufferedBytes;
        if (remainingBytes <= 0)
        {
            IsActive = false;
            return null;
        }

        bool exceedsCap = data.Length > remainingBytes;
        ReadOnlySpan<byte> observedBytes = exceedsCap ? data[..remainingBytes] : data;
        _buffer.Append(Encoding.UTF8.GetString(observedBytes));
        _bufferedBytes += observedBytes.Length;

        string output = _buffer.ToString();

        // The error is looked for first: a chunk can carry both the error and the prompt the
        // host prints after it, and the prompt must not silence the error it follows.
        string? localizationKey = FindDiagnosticKey(output);
        if (localizationKey is not null || exceedsCap || ContainsRemotePowerShellPrompt(output))
        {
            IsActive = false;
        }

        return localizationKey;
    }

    private static string? FindDiagnosticKey(string output)
    {
        // The credential launch runs a local script; a Group Policy that requires signed
        // scripts overrides -ExecutionPolicy Bypass and refuses it before it runs.
        if (output.Contains(ExecutionPolicyHelpTopic, StringComparison.OrdinalIgnoreCase))
        {
            return "ErrorWinRmExecutionPolicyBlocked";
        }

        int ntlmCodeIndex = output.IndexOf(NtlmLoopbackCode, StringComparison.OrdinalIgnoreCase);
        if (ntlmCodeIndex >= 0 && ContainsWinRmContextNear(output, ntlmCodeIndex))
        {
            return "ErrorWinRmNtlmLoopback";
        }

        if (output.Contains(WsManInvalidResponseCode, StringComparison.OrdinalIgnoreCase)
            && ContainsWsManContext(output))
        {
            return "ErrorWinRmWsmanInvalidResponse";
        }

        // The remaining authentication failures are recognised by an untranslated token (a
        // hexadecimal code or the TrustedHosts setting name), never by localized prose, and
        // only next to a WinRM context so an unrelated tool output cannot trigger them.
        foreach ((string token, string key) in AuthenticationDiagnosticTokens)
        {
            int tokenIndex = output.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex >= 0 && ContainsWinRmContextNear(output, tokenIndex, token.Length))
            {
                return key;
            }
        }

        return null;
    }

    private static bool ContainsWinRmContextNear(string output, int diagnosticIndex)
        => ContainsWinRmContextNear(output, diagnosticIndex, NtlmLoopbackCode.Length);

    private static bool ContainsWinRmContextNear(string output, int diagnosticIndex, int tokenLength)
    {
        int start = Math.Max(0, diagnosticIndex - DiagnosticContextRadius);
        int end = Math.Min(output.Length, diagnosticIndex + tokenLength + DiagnosticContextRadius);
        string context = output[start..end];

        return ContainsWsManContext(context)
            || context.Contains("Enter-PSSession", StringComparison.OrdinalIgnoreCase)
            || context.Contains("New-PSSession", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRemotePowerShellPrompt(string output)
        => RemotePromptPattern.IsMatch(output);

    private static bool ContainsWsManContext(string output)
    {
        return output.Contains("WSMan", StringComparison.OrdinalIgnoreCase)
            || output.Contains("WS-Man", StringComparison.OrdinalIgnoreCase)
            || output.Contains("WinRM", StringComparison.OrdinalIgnoreCase);
    }
}
