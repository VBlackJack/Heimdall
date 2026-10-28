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

namespace Heimdall.Core.Security;

/// <summary>
/// A value substituted into the external credential command would not stay one plain argument,
/// so the command was not run.
/// </summary>
/// <remarks>
/// The same rule the external tools follow (<see cref="ArgumentTemplate.FindUnsafeValue"/>): a
/// vault entry name with a space in a template that does not quote <c>{Title}</c> reached the
/// tool as two arguments and was looked up as something else. The command is refused instead of
/// run, and the user told to quote the placeholder.
/// </remarks>
public sealed class CredentialProviderArgumentException : Exception
{
    /// <summary>Initializes the exception for the refused value.</summary>
    /// <param name="rejection">The placeholder whose value is refused, and why.</param>
    public CredentialProviderArgumentException(ArgumentTemplateRejection rejection)
        : base(DescribeRejection(rejection))
    {
        Rejection = rejection;
    }

    /// <summary>The placeholder whose value is refused, and why.</summary>
    public ArgumentTemplateRejection Rejection { get; }

    private static string DescribeRejection(ArgumentTemplateRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return $"The value of {rejection.Token} cannot be passed to the credential command ({rejection.Reason}).";
    }
}
