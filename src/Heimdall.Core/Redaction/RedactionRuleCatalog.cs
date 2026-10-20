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
/// The shipped rule set, in application order. Order matters: a more specific rule runs before a general
/// one so a span is masked, and counted, once (a bearer JWT is the authorization header's, not the jwt rule's).
/// The password prompt rule runs before key-value-secret so an echoed "Password: x" is the prompt rule's.
/// </summary>
public static class RedactionRuleCatalog
{
    public static IReadOnlyList<RedactionRule> Default { get; } = Build();

    private static List<RedactionRule> Build()
    {
        return
        [
            new RedactionRule(
                RedactionRuleIds.PrivateKeyBlock,
                [RedactionPatterns.PrivateKeyMarker()],
                (text, run) => PrivateKeyBlockMasker.Apply(text, RedactionPatterns.PrivateKeyMarker(), RedactionRuleIds.PrivateKeyBlock, run)),

            GroupRule(RedactionRuleIds.AuthorizationHeader, RedactionPatterns.AuthorizationHeader()),

            GroupRule(RedactionRuleIds.UrlCredentials, RedactionPatterns.UrlCredentials()),

            GroupRule(
                RedactionRuleIds.CliPassword,
                RedactionPatterns.CliPasswordOption(),
                RedactionPatterns.CliSshpass(),
                RedactionPatterns.CliExport(),
                RedactionPatterns.CliEnvironmentPassword(),
                RedactionPatterns.CliMysqlPassword()),

            new RedactionRule(
                RedactionRuleIds.PasswordPrompt,
                [RedactionPatterns.PasswordPrompt(), RedactionPatterns.CommandNotFound()],
                (text, run) => PasswordPromptMasker.Apply(
                    text,
                    RedactionPatterns.PasswordPrompt(),
                    RedactionPatterns.CommandNotFound(),
                    RedactionRuleIds.PasswordPrompt,
                    run)),

            GroupRule(RedactionRuleIds.KeyValueSecret, RedactionPatterns.KeyValueSecret()),

            GroupRule(RedactionRuleIds.AwsAccessKey, RedactionPatterns.AwsAccessKey()),

            GroupRule(RedactionRuleIds.GithubToken, RedactionPatterns.GithubToken()),

            GroupRule(RedactionRuleIds.SlackToken, RedactionPatterns.SlackToken()),

            GroupRule(RedactionRuleIds.Jwt, RedactionPatterns.Jwt()),

            new RedactionRule(
                RedactionRuleIds.Ipv4Address,
                [RedactionPatterns.Ipv4Address()],
                (text, run) => PseudonymMasker.ApplyIpv4(text, RedactionPatterns.Ipv4Address(), RedactionRuleIds.Ipv4Address, run),
                requiresPseudonymization: true),

            new RedactionRule(
                RedactionRuleIds.HostName,
                [RedactionPatterns.PromptHost(), RedactionPatterns.FullyQualifiedName()],
                (text, run) => PseudonymMasker.ApplyHostNames(
                    text,
                    RedactionPatterns.PromptHost(),
                    RedactionPatterns.FullyQualifiedName(),
                    RedactionRuleIds.HostName,
                    run),
                requiresPseudonymization: true),
        ];
    }

    private static RedactionRule GroupRule(string id, params Regex[] patterns) =>
        new(id, patterns, RedactionApply.GroupMasker(id, patterns));
}
