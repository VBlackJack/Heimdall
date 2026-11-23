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

        List<string> roots = BuildRoots(allowedFolders);

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

    private static bool UnderAny(string path, List<string> roots)
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

    private List<string> BuildRoots(IReadOnlyList<string> allowedFolders)
    {
        List<string> roots = [];
        AddRoot(_machine, roots, _machine.WindowsDirectory);
        AddRoot(_machine, roots, _machine.ProgramFilesDirectory);
        AddRoot(_machine, roots, _machine.ProgramFilesX86Directory);

        foreach (string folder in allowedFolders)
            AddRoot(_machine, roots, folder);

        return roots;
    }

    private static void AddRoot(IRecipeMachine machine, List<string> roots, string? folder)
    {
        string? full = CanonicalFolder(folder);
        if (full is null)
            return;

        roots.Add(full.TrimEnd(TrimmedRootEnd));

        // A root that is itself reached through a link, a subst drive or a short name is also accepted
        // under its final form, which is the form a file inside it resolves to.
        string? final = machine.ResolveFinalPath(full);
        string? finalFull = final is null ? null : Canonicalize(final);
        if (finalFull is not null && !string.Equals(finalFull.TrimEnd(TrimmedRootEnd), roots[^1], StringComparison.OrdinalIgnoreCase))
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

    private RecipeProgramVerdict ResolveAbsolute(string program, List<string> roots)
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

    private RecipeProgramVerdict ResolveBare(string program, List<string> roots)
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
            if (candidate is null || !UnderAny(candidate, roots) || !_machine.FileExists(candidate))
                continue;

            // The first file found decides: a later refusal is not worked around by trying the next entry.
            return Verify(candidate, program, roots);
        }

        return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotFound, program);
    }

    private static string? AcceptablePathEntry(string entry, List<string> roots)
    {
        string text = entry.Trim();
        if (text.Length >= 2 && text[0] == Quote && text[^1] == Quote)
            text = text[1..^1];

        string? full = CanonicalFolder(text);
        return full is not null && UnderAny(full, roots) ? full : null;
    }

    private RecipeProgramVerdict Verify(string canonical, string program, List<string> roots)
    {
        if (!UnderAny(canonical, roots))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.OutsideAllowedRoots, program);

        if (!HasExecutableExtension(Path.GetFileName(canonical)))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        if (!_machine.FileExists(canonical))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotFound, program);

        string? final = _machine.ResolveFinalPath(canonical);
        string? finalFull = final is null ? null : Canonicalize(final);
        if (finalFull is null)
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.CannotResolve, program);

        if (!UnderAny(finalFull, roots))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.LinkLeavesAllowedRoots, program);

        if (!HasExecutableExtension(Path.GetFileName(finalFull)))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.NotAnExecutable, program);

        if (RecipeSeatBelt.IsRefusedFileName(canonical) || RecipeSeatBelt.IsRefusedFileName(finalFull))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.ShellOrInterpreter, program);

        string? original = _machine.ReadOriginalFileName(finalFull);
        if (original is not null && RecipeSeatBelt.IsRefusedFileName(original))
            return RecipeProgramVerdict.Refused(RecipeProgramRefusal.ShellOrInterpreterByOriginalName, program);

        return RecipeProgramVerdict.Resolved(finalFull);
    }
}
