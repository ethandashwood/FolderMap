using System.Diagnostics;
using System.Runtime.InteropServices;
using FolderMap.Models;

namespace FolderMap.Services;

/// <summary>Real file-system actions. Each throws on failure; the UI shows the message.</summary>
public static class FileOps
{
    // File types that run something when "opened" instead of just showing it.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".msc", ".scr", ".cpl", ".hta", ".jar", ".reg", ".lnk", ".url", ".py", ".pyw",
        ".application", ".appref-ms", ".sh", ".appx", ".msix",
    };

    // Names Windows reserves for devices; files can't be called these.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True if opening this file would run a program or script.</summary>
    public static bool IsRunnable(string path) => RunnableExtensions.Contains(Path.GetExtension(path));

    /// <summary>Open with the default app. Callers should confirm first when <see cref="IsRunnable"/> is true.</summary>
    public static void Open(string path)
    {
        EnsureExists(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// Show a code file at a line: in VS Code if it's installed, otherwise in Notepad.
    /// Never hands code to Windows' default app, because for .py / .js that would RUN it.
    /// </summary>
    public static void OpenInEditor(string path, int line)
    {
        EnsureExists(path);

        var vsCode = FindVsCode();
        if (vsCode != null)
        {
            var start = new ProcessStartInfo(vsCode) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--goto");
            start.ArgumentList.Add($"{path}:{line}");
            Process.Start(start);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var notepad = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            notepad.ArgumentList.Add(path);
            Process.Start(notepad);
        }
        else
        {
            ShowInExplorer(path); // just show where it is
        }
    }

    /// <summary>
    /// Find VS Code. Prefers Code.exe itself over the code.cmd launcher, because going through
    /// cmd.exe would expand things like %TEMP% inside file names.
    /// </summary>
    private static string? FindVsCode()
    {
        if (!OperatingSystem.IsWindows()) return FindOnPath("code");

        var cmd = FindOnPath("code.cmd");
        if (cmd != null)
        {
            // Standard install layout: <VS Code>\bin\code.cmd next to <VS Code>\Code.exe
            var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmd)!, "..", "Code.exe"));
            if (File.Exists(exe)) return exe;
        }
        return FindOnPath("code.exe");
    }

    private static string? FindOnPath(string name)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    public static void ShowInExplorer(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
    }

    /// <summary>Checks a new file or folder name. Returns null if it's fine, otherwise the reason.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The name can't be empty.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*', '\\', '/' }) >= 0)
            return "That name contains characters that aren't allowed in file names ( < > : \" / \\ | ? * ).";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "Windows doesn't allow names that end with a dot or a space.";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            return $"\"{Path.GetFileNameWithoutExtension(name)}\" is a name Windows reserves for devices.";
        if (name.Length > 255) return "That name is too long.";
        return null;
    }

    public static void Rename(FsNode node, string newName)
    {
        if (ValidateName(newName) is { } problem) throw new ArgumentException(problem);

        var source = node.FullPath;
        EnsureExists(source);
        var target = Path.Combine(Path.GetDirectoryName(source)!, newName);

        bool caseOnly = !string.Equals(source, target, StringComparison.Ordinal)
                        && string.Equals(source, target, StringComparison.OrdinalIgnoreCase);

        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException($"Something called \"{newName}\" already exists there.");

        if (caseOnly)
        {
            // Windows treats "photo.JPG" and "photo.jpg" as the same name, so go via a temporary name.
            var temp = Path.Combine(Path.GetDirectoryName(source)!, $"~rename-{Guid.NewGuid():N}");
            MovePath(node.IsFolder, source, temp);
            try
            {
                MovePath(node.IsFolder, temp, target);
            }
            catch
            {
                MovePath(node.IsFolder, temp, source); // put it back as it was
                throw;
            }
            return;
        }

        MovePath(node.IsFolder, source, target);
    }

    private static void MovePath(bool isFolder, string from, string to)
    {
        if (isFolder) Directory.Move(from, to);
        else File.Move(from, to);
    }

    public static void Move(FsNode node, string destinationFolder)
    {
        var source = node.FullPath;
        EnsureExists(source);
        if (!Directory.Exists(destinationFolder))
            throw new DirectoryNotFoundException($"The folder \"{destinationFolder}\" doesn't exist any more.");

        var target = Path.Combine(destinationFolder, node.Name);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"\"{node.Name}\" already exists in that folder.");

        if (node.IsFolder)
        {
            if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Moving folders between drives isn't supported yet. Move the files inside instead, or use File Explorer.");
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target); // File.Move copies across drives automatically
        }
    }

    /// <summary>
    /// True for drives that usually have no Recycle Bin (USB sticks, network drives, CDs),
    /// where "recycling" would really mean deleting permanently.
    /// </summary>
    public static bool MayNotHaveRecycleBin(string path)
    {
        try
        {
            if (path.StartsWith(@"\\")) return true; // \\server\share
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;
            var type = new DriveInfo(root).DriveType;
            return type is DriveType.Removable or DriveType.Network or DriveType.CDRom or DriveType.Ram or DriveType.Unknown;
        }
        catch
        {
            return true; // if in doubt, warn
        }
    }

    /// <summary>
    /// Sends to the Recycle Bin. If Windows can't recycle it (no Recycle Bin on that drive,
    /// or it's too big), Windows asks before deleting permanently - it never happens silently.
    /// Throws <see cref="OperationCanceledException"/> if the user says no.
    /// </summary>
    public static void Recycle(FsNode node)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sending to the Recycle Bin is only supported on Windows.");
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Recycling needs the 64-bit version of the app.");

        var path = node.FullPath;
        EnsureExists(path);

        var op = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0", // must be double-null-terminated; the marshaller adds the second
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT,
        };

        int result = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) throw new OperationCanceledException();
        if (result != 0) throw new IOException($"Windows couldn't recycle it (error 0x{result:X}).");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("It's still there. Windows may have skipped it because it's in use.");
    }

    private static void EnsureExists(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException(
                $"\"{Path.GetFileName(path)}\" isn't there any more. It may have been moved or deleted since the scan, so try scanning again.");
    }

    // ----- Windows shell API (the same call File Explorer uses to delete) -----

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;            // no progress window
    private const ushort FOF_NOCONFIRMATION = 0x0010;    // we already asked the user
    private const ushort FOF_ALLOWUNDO = 0x0040;         // = use the Recycle Bin
    private const ushort FOF_WANTNUKEWARNING = 0x4000;   // ...but ASK before permanently deleting

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
