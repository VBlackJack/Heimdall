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

using System.Runtime.InteropServices;
using Heimdall.Core.Recipes;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>A description of a machine that lives only in memory, so that no test depends on the real one.</summary>
internal sealed class FakeRecipeMachine : IRecipeMachine
{
    public string WindowsDirectory { get; set; } = @"C:\Windows";

    public string ProgramFilesDirectory { get; set; } = @"C:\Program Files";

    public string ProgramFilesX86Directory { get; set; } = @"C:\Program Files (x86)";

    public List<string> Path { get; } = [];

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Final path of a file that is a link; a file not listed here is its own final path.</summary>
    public Dictionary<string, string?> Finals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> OriginalNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path the resolver asked about, to prove where it did not look.</summary>
    public List<string> Probed { get; } = [];

    public IReadOnlyList<string> PathEntries => Path;

    public bool FileExists(string path)
    {
        Probed.Add(path);
        return Files.Contains(path);
    }

    public string? ResolveFinalPath(string path) => Finals.TryGetValue(path, out string? final) ? final : path;

    public string? ReadOriginalFileName(string path) => OriginalNames.GetValueOrDefault(path);

    /// <summary>The permission entries of a file or folder; one that is not listed grants nothing but to the trusted principals.</summary>
    public Dictionary<string, List<RecipeAccessRule>> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose permissions cannot be read.</summary>
    public HashSet<string> UnreadablePermissions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path whose permissions the resolver asked about, in order.</summary>
    public List<string> PermissionReads { get; } = [];

    public IReadOnlyList<RecipeAccessRule>? ReadAllowRules(string path)
    {
        PermissionReads.Add(path);
        if (UnreadablePermissions.Contains(path))
            return null;

        return Rules.TryGetValue(path, out List<RecipeAccessRule>? rules) ? rules : [];
    }

    public FakeRecipeMachine WithFile(string path)
    {
        Files.Add(path);
        return this;
    }

    /// <summary>Gives a principal an access mask on a file or folder.</summary>
    public FakeRecipeMachine WithRule(string path, string sid, int mask, bool inheritOnly = false)
    {
        if (!Rules.TryGetValue(path, out List<RecipeAccessRule>? rules))
        {
            rules = [];
            Rules[path] = rules;
        }

        rules.Add(new RecipeAccessRule(sid, mask, inheritOnly));
        return this;
    }
}

/// <summary>A machine whose roots are temporary folders and whose file system is the real one.</summary>
internal sealed class TempRecipeMachine : IRecipeMachine
{
    private readonly RecipeMachine _real = RecipeMachine.Instance;

    public required string WindowsDirectory { get; init; }

    public string ProgramFilesDirectory { get; init; } = string.Empty;

    public string ProgramFilesX86Directory { get; init; } = string.Empty;

    public List<string> Path { get; } = [];

    public IReadOnlyList<string> PathEntries => Path;

    public bool FileExists(string path) => _real.FileExists(path);

    public string? ResolveFinalPath(string path) => _real.ResolveFinalPath(path);

    public string? ReadOriginalFileName(string path) => _real.ReadOriginalFileName(path);

    /// <summary>The temporary folder is writable by its user by nature: its permissions are not what these tests are about.</summary>
    public IReadOnlyList<RecipeAccessRule>? ReadAllowRules(string path) => [];
}

/// <summary>A scratch folder under the temporary directory, removed with the test.</summary>
internal sealed class ScratchFolder : IDisposable
{
    private readonly List<string> _junctions = [];

    public ScratchFolder()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "heimdall-recipe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        string path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a file with arbitrary bytes; it is never executed.</summary>
    public string File(string relative)
    {
        string path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01]);
        return path;
    }

    /// <summary>Creates a directory junction, which needs no elevation.</summary>
    public string Junction(string relativeLink, string target)
    {
        string link = System.IO.Path.Combine(Root, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(link);
        JunctionNative.SetMountPoint(link, target);
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string junction in _junctions)
        {
            try
            {
                Directory.Delete(junction);
            }
            catch (IOException)
            {
                // Best effort: the scratch root is removed below anyway.
            }
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless.
        }
    }
}

/// <summary>Sets a mount point (junction) reparse point on an empty directory.</summary>
internal static class JunctionNative
{
    private const uint GenericWrite = 0x40000000;

    private const uint ShareAll = 0x7;

    private const uint OpenExisting = 3;

    private const uint BackupSemanticsAndOpenReparsePoint = 0x02000000 | 0x00200000;

    private const uint FsctlSetReparsePoint = 0x000900A4;

    private const uint MountPointTag = 0xA0000003;

    private const string NtPrefix = @"\??\";

    private const int HeaderBytes = 8;

    private const int MountPointFieldsBytes = 8;

    private const int NulBytes = 2;

    public static void SetMountPoint(string link, string target)
    {
        string substitute = NtPrefix + System.IO.Path.GetFullPath(target).TrimEnd('\\');
        int substituteBytes = substitute.Length * 2;
        int dataLength = MountPointFieldsBytes + substituteBytes + NulBytes + NulBytes;
        byte[] buffer = new byte[HeaderBytes + dataLength];

        BitConverter.GetBytes(MountPointTag).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substituteBytes).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteBytes + NulBytes)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 14);
        System.Text.Encoding.Unicode.GetBytes(substitute).CopyTo(buffer, 16);

        using SafeFileHandle handle = CreateFileW(link, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Could not open the folder to make a junction.", Marshal.GetLastWin32Error());

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Could not make the junction.", Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle,
        uint code,
        byte[] input,
        int inputSize,
        IntPtr output,
        int outputSize,
        out int returned,
        IntPtr overlapped);
}
