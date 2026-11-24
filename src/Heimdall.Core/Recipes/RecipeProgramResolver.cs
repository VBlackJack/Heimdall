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

namespace Heimdall.Core.Recipes;

/// <summary>Why a program was refused. <see cref="None"/> means it resolved.</summary>
public enum RecipeProgramRefusal
{
    /// <summary>The program resolved to an allowed executable.</summary>
    None,

    /// <summary>The program text is empty.</summary>
    Empty,

    /// <summary>Not a bare file name and not an absolute drive path (relative, UNC, device, rooted without a drive, too long, odd segment).</summary>
    InvalidShape,

    /// <summary>The text holds a control or format character, or a percent sign.</summary>
    ForbiddenCharacter,

    /// <summary>The file name does not end in exactly <c>.exe</c>.</summary>
    NotAnExecutable,

    /// <summary>No such file in the places that may be searched.</summary>
    NotFound,

    /// <summary>The path lies outside the allowed folders.</summary>
    OutsideAllowedRoots,

    /// <summary>The path lies inside the allowed folders, but a junction or link leads outside them.</summary>
    LinkLeavesAllowedRoots,

    /// <summary>The final path of the file could not be determined.</summary>
    CannotResolve,

    /// <summary>The file name is a shell, an interpreter or a proxy executable (see <see cref="RecipeSeatBelt"/>).</summary>
    ShellOrInterpreter,

    /// <summary>The original file name in the version resource is a shell, an interpreter or a proxy executable.</summary>
    ShellOrInterpreterByOriginalName,

    /// <summary>
    /// The file, or a folder between it and the root it sits under, lets someone who is not an
    /// administrator change it, so what runs could be swapped without any approval.
    /// </summary>
    WritableByUser,

    /// <summary>The permissions of the file or of a folder above it could not be read.</summary>
    CannotReadPermissions,
}

/// <summary>The outcome of resolving a program.</summary>
/// <param name="Refusal">Why the program was refused, or <see cref="RecipeProgramRefusal.None"/>.</param>
/// <param name="ResolvedPath">The absolute final path, set only when <paramref name="Refusal"/> is None.</param>
/// <param name="Fragment">A short safe-to-display piece of the offending text, or empty.</param>
public sealed record RecipeProgramVerdict(RecipeProgramRefusal Refusal, string? ResolvedPath, string Fragment)
{
    /// <summary>True when the program resolved.</summary>
    public bool IsResolved => Refusal == RecipeProgramRefusal.None && ResolvedPath is not null;

    internal static RecipeProgramVerdict Resolved(string path) => new(RecipeProgramRefusal.None, path, string.Empty);

    internal static RecipeProgramVerdict Refused(RecipeProgramRefusal refusal, string? text)
        => new(refusal, null, RecipeText.MakeSafe(text));
}

/// <summary>How a program resolves now compared with the path stored at import.</summary>
public enum RecipeProgramComparison
{
    /// <summary>It resolves to the stored path.</summary>
    Same,

    /// <summary>It resolves, to another path.</summary>
    Different,

    /// <summary>It no longer resolves (missing, moved outside the roots, or refused).</summary>
    NoLongerResolves,
}

/// <summary>
/// Turns the <c>program</c> text of a recipe into an absolute path that may run, or a refusal.
/// The operating system search order is never used: Heimdall looks in a fixed, short list of places.
/// </summary>
/// <remarks>
/// A TOCTOU gap remains between the answer and the launch (the file can be swapped in between); the
/// runner is given the final path so that a junction swapped later cannot redirect it, but a file
/// replaced inside an allowed folder is not detected here.
/// </remarks>
public sealed class RecipeProgramResolver : IRecipeProgramResolver, IRecipeProgramLocator
{
    private const string ExecutableExtension = ".exe";

    private const char DriveSeparator = ':';

    private const int DriveRootLength = 3;

    private const int DriveColonIndex = 1;

    private const char Quote = '"';

    private const string ForbiddenNameCharacters = "<>\"|*?";

    private static readonly char[] Separators = ['\\', '/'];

    private static readonly char[] ForbiddenNameChars = ForbiddenNameCharacters.ToCharArray();

    private static readonly char[] TrimmedRootEnd = ['\\', '/'];

    private readonly IRecipeMachine _machine;

    /// <summary>Creates a resolver over the real machine.</summary>
    public RecipeProgramResolver()
        : this(RecipeMachine.Instance)
    {
    }

    /// <summary>Creates a resolver over the given machine description.</summary>
    public RecipeProgramResolver(IRecipeMachine machine)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    }

    /// <summary>Resolves a program.</summary>
    /// <param name="program">The <c>program</c> text of the recipe, as written.</param>
    /// <param name="allowedFolders">The folders the user added in Settings; may be empty.</param>
    public RecipeProgramVerdict Resolve(string? program, IReadOnlyList<string> allowedFolders)
    {
        ArgumentNullException.ThrowIfNull(allowedFolders);

        if (string.IsNullOrWhiteSpace(program))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.Empty, program);

        if (program.Contains('%', StringComparison.Ordinal) || RecipeText.ContainsRefusedCharacter(program))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.ForbiddenCharacter, program);

        if (program.Length > RecipeLimits.MaxProgramLength)
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.InvalidShape, program);

        ProgramRoots roots = BuildRoots(allowedFolders);

        if (IsDrivePathShape(program))
            return ResolveAbsolute(program, roots);

        if (IsBareName(program))
            return ResolveBare(program, roots);

        return RecipeProgramVerdict.Refused(RecipeProgramRefusal.InvalidShape, program);
    }

    /// <summary>Resolves the program again and compares the result with the path stored at import.</summary>
    /// <param name="program">The <c>program</c> text of the recipe.</param>
    /// <param name="allowedFolders">The folders the user added in Settings.</param>
    /// <param name="storedPath">The resolved path stored when the recipe was imported.</param>
    public RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath)
    {
        ArgumentNullException.ThrowIfNull(storedPath);

        RecipeProgramVerdict verdict = Resolve(program, allowedFolders);
        if (!verdict.IsResolved)
            return RecipeProgramComparison.NoLongerResolves;

        return string.Equals(verdict.ResolvedPath, storedPath, StringComparison.OrdinalIgnoreCase)
            ? RecipeProgramComparison.Same
            : RecipeProgramComparison.Different;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or lies inside it. The test is
    /// ordinal, ignores case, and looks at whole path segments: <c>C:\Program Files Evil</c> is not
    /// under <c>C:\Program Files</c>.
    /// </summary>
    internal static bool IsUnderOrEqual(string path, string root)
    {
        string trimmed = root.TrimEnd(TrimmedRootEnd);
        if (trimmed.Length == 0)
            return false;

        if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == trimmed.Length || path[trimmed.Length] is '\\' or '/';
    }

    private static bool IsDrivePathShape(string text)
        => text.Length >= DriveRootLength
            && char.IsAsciiLetter(text[0])
            && text[DriveColonIndex] == DriveSeparator
            && text[DriveColonIndex + 1] is '\\' or '/';

    private static bool IsBareName(string text)
        => text.IndexOfAny(Separators) < 0
            && text.IndexOf(DriveSeparator) < 0
            && text.IndexOfAny(ForbiddenNameChars) < 0
            && text is not ("." or "..")
            && text == text.Trim();

    private static bool HasExecutableExtension(string fileName)
        => fileName.Length > ExecutableExtension.Length
            && fileName.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase);

    private static bool UnderAny(string path, IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
        {
            if (IsUnderOrEqual(path, root))
                return true;
        }

        return false;
    }

    private static string? Canonicalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private ProgramRoots BuildRoots(IReadOnlyList<string> allowedFolders)
    {
        ProgramRoots roots = new();
        AddRoot(_machine, roots.BuiltIn, _machine.WindowsDirectory);
        AddRoot(_machine, roots.BuiltIn, _machine.ProgramFilesDirectory);
        AddRoot(_machine, roots.BuiltIn, _machine.ProgramFilesX86Directory);

        // A folder the user added is only taken as written when it still passes the rule it was
        // added under: a hand-edited setting (a path with .. parts, a drive root) never widens the roots.
        foreach (string folder in allowedFolders)
        {
            if (RecipeFolderRule.Check(folder) == RecipeFolderRefusal.None)
                AddRoot(_machine, roots.User, RecipeFolderRule.Normalize(folder));
        }

        return roots;
    }

    private static void AddRoot(IRecipeMachine machine, List<string> roots, string? folder)
    {
        string? full = CanonicalFolder(folder);
        if (full is null)
            return;

        string trimmed = full.TrimEnd(TrimmedRootEnd);
        roots.Add(trimmed);

        // A root that is itself reached through a link, a subst drive or a short name is also accepted
        // under its final form, which is the form a file inside it resolves to.
        string? final = machine.ResolveFinalPath(full);
        string? finalFull = final is null ? null : Canonicalize(final);
        if (finalFull is not null && !string.Equals(finalFull.TrimEnd(TrimmedRootEnd), trimmed, StringComparison.OrdinalIgnoreCase))
            roots.Add(finalFull.TrimEnd(TrimmedRootEnd));
    }

    /// <summary>
    /// The canonical form of a folder given as an absolute drive path, or null for anything else. A bare
    /// drive (<c>D:</c>) means the root of the drive, never the current folder on it.
    /// </summary>
    private static string? CanonicalFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)
            || folder.Contains('%', StringComparison.Ordinal)
            || RecipeText.ContainsRefusedCharacter(folder))
        {
            return null;
        }

        string text = folder.Trim();
        if (text.Length == DriveColonIndex + 1 && char.IsAsciiLetter(text[0]) && text[DriveColonIndex] == DriveSeparator)
            text += '\\';

        return IsDrivePathShape(text) ? Canonicalize(text) : null;
    }

    private RecipeProgramVerdict ResolveAbsolute(string program, ProgramRoots roots)
    {
        // Every folder segment must be an ordinary name: no empty one, no trailing dot or space, which
        // Windows would silently trim into a different folder.
        string[] segments = program[DriveRootLength..].Split(Separators);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            string segment = segments[index];
            bool dots = segment is "." or "..";
            if (segment.Length == 0
                || segment.IndexOfAny(ForbiddenNameChars) >= 0
                || segment.IndexOf(DriveSeparator) >= 0
                || (!dots && segment[^1] is ' ' or '.'))
            {
                return RecipeProgramVerdict.Refused(RecipeProgramRefusal.InvalidShape, program);
            }
        }

        string fileName = segments[^1];
        if (fileName.IndexOf(DriveSeparator) >= 0 || fileName.IndexOfAny(ForbiddenNameChars) >= 0)
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        if (!HasExecutableExtension(fileName))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        string? canonical = Canonicalize(program);
        if (canonical is null)
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.InvalidShape, program);

        return Verify(canonical, program, roots);
    }

    private RecipeProgramVerdict ResolveBare(string program, ProgramRoots roots)
    {
        string fileName = program;
        if (fileName.IndexOf('.') < 0)
            fileName += ExecutableExtension;

        if (!HasExecutableExtension(fileName))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        // Only the PATH entries that themselves lie under an allowed root are searched, in PATH order.
        // The current directory and the application directory are never searched.
        foreach (string entry in _machine.PathEntries)
        {
            string? directory = AcceptablePathEntry(entry, roots);
            if (directory is null)
                continue;

            string? candidate = Canonicalize(Path.Combine(directory, fileName));
            if (candidate is null || !UnderAny(candidate, roots.All) || !_machine.FileExists(candidate))
                continue;

            // The first file found decides: a later refusal is not worked around by trying the next entry.
            return Verify(candidate, program, roots);
        }

        return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotFound, program);
    }

    private static string? AcceptablePathEntry(string entry, ProgramRoots roots)
    {
        string text = entry.Trim();
        if (text.Length >= 2 && text[0] == Quote && text[^1] == Quote)
            text = text[1..^1];

        string? full = CanonicalFolder(text);
        return full is not null && UnderAny(full, roots.All) ? full : null;
    }

    private RecipeProgramVerdict Verify(string canonical, string program, ProgramRoots roots)
    {
        if (!UnderAny(canonical, roots.All))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.OutsideAllowedRoots, program);

        if (!HasExecutableExtension(Path.GetFileName(canonical)))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        if (!_machine.FileExists(canonical))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotFound, program);

        string? final = _machine.ResolveFinalPath(canonical);
        string? finalFull = final is null ? null : Canonicalize(final);
        if (finalFull is null)
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.CannotResolve, program);

        if (!UnderAny(finalFull, roots.All))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.LinkLeavesAllowedRoots, program);

        if (!HasExecutableExtension(Path.GetFileName(finalFull)))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        if (RecipeSeatBelt.IsRefusedFileName(canonical) || RecipeSeatBelt.IsRefusedFileName(finalFull))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.ShellOrInterpreter, program);

        string? original = _machine.ReadOriginalFileName(finalFull);
        if (original is not null && RecipeSeatBelt.IsRefusedFileName(original))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.ShellOrInterpreterByOriginalName, program);

        RecipeProgramRefusal permission = CheckPermissions(finalFull, roots);
        if (permission != RecipeProgramRefusal.None)
            return RecipeProgramVerdict.Refused(permission, program);

        return RecipeProgramVerdict.Resolved(finalFull);
    }

    /// <summary>
    /// Under Windows or Program Files, the file and every folder from it up to its root must not let
    /// a user who is not an administrator change them: some folders there are writable by anyone.
    /// A folder the user added is trusted only outside those roots: adding one that covers part of
    /// Windows or Program Files does not switch the check off for what lies under it.
    /// </summary>
    private RecipeProgramRefusal CheckPermissions(string finalPath, ProgramRoots roots)
    {
        string? root = null;
        foreach (string candidate in roots.BuiltIn)
        {
            if (IsUnderOrEqual(finalPath, candidate) && (root is null || candidate.Length > root.Length))
                root = candidate;
        }

        if (root is null)
            return UnderAny(finalPath, roots.User) ? RecipeProgramRefusal.None : RecipeProgramRefusal.OutsideAllowedRoots;

        string? current = finalPath;
        while (current is not null && IsUnderOrEqual(current, root))
        {
            IReadOnlyList<RecipeAccessRule>? rules = _machine.ReadAllowRules(current);
            if (rules is null)
                return RecipeProgramRefusal.CannotReadPermissions;

            if (RecipeAccessPolicy.GrantsModificationToStandardUsers(rules))
                return RecipeProgramRefusal.WritableByUser;

            if (string.Equals(current.TrimEnd(TrimmedRootEnd), root, StringComparison.OrdinalIgnoreCase))
                break;

            current = Path.GetDirectoryName(current);
        }

        return RecipeProgramRefusal.None;
    }

    /// <summary>The folders a program may sit under: the built-in ones, and the ones the user added.</summary>
    private sealed class ProgramRoots
    {
        public List<string> BuiltIn { get; } = [];

        public List<string> User { get; } = [];

        public IReadOnlyList<string> All => [.. BuiltIn, .. User];
    }
}
