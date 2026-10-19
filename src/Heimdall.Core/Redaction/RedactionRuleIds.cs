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

namespace Heimdall.Core.Redaction;

/// <summary>
/// Stable rule identifiers. They appear inside mask tokens and in <see cref="RedactionResult.Counts"/>,
/// so renaming one is a format change: they are pinned by tests.
/// </summary>
public static class RedactionRuleIds
{
    public const string PrivateKeyBlock = "private-key-block";

    public const string AuthorizationHeader = "authorization-header";

    public const string UrlCredentials = "url-credentials";

    public const string CliPassword = "cli-password";

    public const string KeyValueSecret = "key-value-secret";

    public const string AwsAccessKey = "aws-access-key";

    public const string GithubToken = "github-token";

    public const string SlackToken = "slack-token";

    public const string Jwt = "jwt";

    public const string PasswordPrompt = "password-prompt";

    public const string Ipv4Address = "ipv4-address";

    public const string HostName = "host-name";
}
