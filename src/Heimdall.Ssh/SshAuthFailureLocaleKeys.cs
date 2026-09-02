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

namespace Heimdall.Ssh;

/// <summary>
/// Localization keys for the sentence Heimdall appends after a gateway's own
/// refusal wording. Each one states an observation - what the local SSH agent
/// held when the dial was made - and none of them claims to name the cause.
/// <para>
/// They live in a class named for locale keys so the C#-to-catalogue guard
/// discovers them: see <c>CSharpLocaleKeyCoverageTests</c>, which fails when a
/// key referenced from C# is missing from en.json or fr.json.
/// </para>
/// </summary>
public static class SshAuthFailureLocaleKeys
{
    /// <summary>
    /// Appended when no reachable agent held any identity. It says that no agent
    /// key was offered, which is provable, and leaves both remedies open: the
    /// saved sign-in details may be wrong, or the expected agent key may simply
    /// not be loaded.
    /// </summary>
    public const string NoAgentKeyLoaded = "ErrorSshAuthContextNoAgentKeyLoaded";

    /// <summary>
    /// Appended when exactly one agent identity was available. Naming the count
    /// distinguishes this from the empty-agent case: an agent is running and
    /// holds a key, and the gateway still refused the sign-in.
    /// </summary>
    public const string OneAgentKeyRefused = "ErrorSshAuthContextAgentKeyRefused";

    /// <summary>
    /// Appended when several agent identities were available. Carries the count
    /// as <c>{0}</c>.
    /// </summary>
    public const string ManyAgentKeysRefused = "ErrorSshAuthContextAgentKeysRefused";
}
