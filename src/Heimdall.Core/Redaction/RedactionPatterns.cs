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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Redaction;

/// <summary>
/// Every pattern of the catalogue. All are line-oriented ([ \t] never \s between tokens), every quantifier
/// is bounded, none nests an unbounded quantifier, and each declares <see cref="RedactionRule.MatchTimeoutMilliseconds"/>.
/// The group named <c>v</c> is the span that gets masked.
/// </summary>
internal static partial class RedactionPatterns
{
    private const RegexOptions Plain = RegexOptions.CultureInvariant;

    private const RegexOptions Insensitive = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // Upper bound for a secret value; longer runs are still masked up to this length.
    private const string MaxValue = "4096";

    // Upper bound for a token carried in a header (JWTs can exceed 4 KiB).
    private const string MaxToken = "8192";

    // A quoted value (mask inside the quotes) or a bare value up to whitespace; empty values never match.
    private const string QuotedOrBare =
        "(?:\"(?<v>[^\"\\r\\n]{1," + MaxValue + "})\"|'(?<v>[^'\\r\\n]{1," + MaxValue + "})'|(?<v>[^\\s\"']{1," + MaxValue + "}))";

    // Same, for a bare value that must not start with '-' (an option, not a password) or a quote.
    private const string QuotedOrBareNoOption =
        "(?:\"(?<v>[^\"\\r\\n]{1," + MaxValue + "})\"|'(?<v>[^'\\r\\n]{1," + MaxValue + "})'|(?<v>[^\\s\"'-][^\\s\"']{0," + MaxValue + "}))";

    // Key-value form: a bare value also stops at ';', ',' and '&' (query strings, lists).
    private const string QuotedOrBareDelimited =
        "(?:\"(?<v>[^\"\\r\\n]{1," + MaxValue + "})\"|'(?<v>[^'\\r\\n]{1," + MaxValue + "})'|(?<v>[^\\s;,&\"']{1," + MaxValue + "}))";

    private const string MysqlCommands = "(?:mysql|mysqldump|mysqladmin|mariadb)";

    private const string SecretKeyEnding =
        "(?:password|passwd|secret|token|api_key|apikey|api-key|access_key|accesskey|private_key|privatekey|client-key-data|client_secret)";

    private const string PromptWords = "(?:password|passphrase|mot de passe|passwort|contrase(?:na|\\u00F1a))";

    private const string PromptForWords = "(?:for|pour|f\\u00FCr|para)";

    private const string HostLabel = "[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?";

    [GeneratedRegex(
        "-----(?<k>BEGIN|END) [A-Z ]{0,40}PRIVATE KEY-----",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex PrivateKeyMarker();

    [GeneratedRegex(
        "Authorization:[ \\t]{0,16}(?:Bearer[ \\t]{1,16}(?<v>[A-Za-z0-9._~+/=-]{8," + MaxToken + "})|Basic[ \\t]{1,16}(?<v>[A-Za-z0-9+/=]{8," + MaxToken + "}))",
        Insensitive,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        "://[^/\\s:@]{1,256}:(?<v>[^/\\s@]{1,1024})@",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex UrlCredentials();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])--(?:password|passwd)(?:=|[ \\t]{1,16})" + QuotedOrBare,
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CliPasswordOption();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])sshpass[ \\t]{1,16}-p[ \\t]{0,16}" + QuotedOrBare,
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CliSshpass();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_])export[ \\t]{1,16}[A-Za-z0-9_]{0,60}(?:PASSWORD|PASSWD|_PWD)=" + QuotedOrBare,
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CliExport();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_])(?:PGPASSWORD|MYSQL_PWD)=" + QuotedOrBare,
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CliEnvironmentPassword();

    // Case-sensitive on purpose: -P is the port, -p is the password.
    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])" + MysqlCommands + "(?=[ \\t])[^\\r\\n]{0,512}?[ \\t]-p[ \\t]?" + QuotedOrBareNoOption,
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CliMysqlPassword();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])[A-Za-z0-9_.-]{0,40}" + SecretKeyEnding + "[\"']?[ \\t]{0,16}[=:][ \\t]{0,16}" + QuotedOrBareDelimited,
        Insensitive,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex KeyValueSecret();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?<v>(?:AKIA|ASIA)[0-9A-Z]{16})(?![0-9A-Z])",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex AwsAccessKey();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_])(?<v>gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{22,255})",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex GithubToken();

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?<v>xox[baprs]-[A-Za-z0-9-]{10,255})",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex SlackToken();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])(?<v>eyJ[A-Za-z0-9_-]{8," + MaxToken + "}\\.[A-Za-z0-9_-]{8," + MaxToken + "}\\.[A-Za-z0-9_-]{8," + MaxToken + "})",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex Jwt();

    // A prompt word, optionally "for <who>", then a colon followed by a blank or the end of the line.
    [GeneratedRegex(
        "(?<![A-Za-z])" + PromptWords + "(?:[ \\t]{1,8}" + PromptForWords + "[ \\t]{1,8}[^\\r\\n]{1,128}?)?[ \\t]{0,8}:(?=[ \\t]|$)[ \\t]{0,64}",
        Insensitive,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex PasswordPrompt();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])(?:-?bash|zsh|sh|ksh|fish): (?<v>\\S{1,256}): command not found",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex CommandNotFound();

    [GeneratedRegex(
        "(?<![0-9])(?<![0-9]\\.)(?<v>[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3})(?![0-9])(?!\\.[0-9])",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex Ipv4Address();

    // The host part of user@HOST prompt forms.
    [GeneratedRegex(
        "(?<![A-Za-z0-9_.-])[A-Za-z_][A-Za-z0-9_.-]{0,31}@(?<v>" + HostLabel + "(?:\\." + HostLabel + "){0,5})(?![A-Za-z0-9-])(?!\\.[A-Za-z0-9])",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex PromptHost();

    // A fully qualified name: at least two dots, alphabetic last label of 2 to 24 characters.
    [GeneratedRegex(
        "(?<![A-Za-z0-9_.@-])(?<v>(?:" + HostLabel + "\\.){2,5}[A-Za-z]{2,24})(?![A-Za-z0-9-])(?!\\.[A-Za-z0-9])",
        Plain,
        RedactionRule.MatchTimeoutMilliseconds)]
    internal static partial Regex FullyQualifiedName();
}
