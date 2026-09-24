using System.Diagnostics;
using FolderMap.Models;
using Microsoft.VisualBasic.FileIO;

namespace FolderMap.Services;

/// <summary>Real file-system actions. Each throws on failure; the UI shows the message.</summary>
public static class FileOps
{
    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowInExplorer(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Open(Path.GetDirectoryName(path) ?? path);
    }

    public static void Rename(FsNode node, string newName)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("That name contains characters that aren't allowed in file names.");

        var source = node.FullPath;
        var target = Path.Combine(Path.GetDirectoryName(source)!, newName);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"Something called \"{newName}\" already exists there.");

        if (node.IsFolder) Directory.Move(source, target);
        else File.Move(source, target);
    }

    public static void Move(FsNode node, string destinationFolder)
    {
        var source = node.FullPath;
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

    /// <summary>Sends to the Recycle Bin (never deletes permanently).</summary>
    public static void Recycle(FsNode node)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Sending to the Recycle Bin is only supported on Windows.");

        var path = node.FullPath;
        if (node.IsFolder)
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}
