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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Describes the severity of a structured OpenSSH import diagnostic.
/// </summary>
public enum OpenSshDiagnosticLevel
{
    Info,
    Warning
}

/// <summary>
/// Identifies a specific OpenSSH import diagnostic condition.
/// </summary>
public enum OpenSshDiagnosticCode
{
    MatchBlockIgnored,
    IncludeDirectiveIgnored,
    WildcardAliasIgnored,
    UnknownDirectiveIgnored,
    InvalidPort,
    DuplicateAliasInFile,
    ProxyJumpCapturedButNotMapped,
    ProxyCommandUnsupported,
    ProxyJumpMixedWithProxyCommand,
    ProxyJumpTokenSubstitution,
    ProxyJumpCycle,
    ProxyJumpUnrecognizedSyntax,
    IdentityFileTildeExpanded,
    HostNameFallbackToAlias,
    HostNameTokenSubstitution
}

/// <summary>
/// Represents a structured parser or import diagnostic.
/// </summary>
/// <param name="Level">Severity level.</param>
/// <param name="LineNumber">1-based line number in the source file.</param>
/// <param name="Code">Structured diagnostic code.</param>
/// <param name="Context">Optional context string for message formatting.</param>
public sealed record OpenSshImportDiagnostic(
    OpenSshDiagnosticLevel Level,
    int LineNumber,
    OpenSshDiagnosticCode Code,
    string? Context = null);
