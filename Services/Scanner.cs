using FolderMap.Models;

namespace FolderMap.Services;

public sealed record ScanProgress(long Folders, long Files, long Bytes, string Current);

/// <summary>Walks a folder and builds the in-memory tree with sizes.</summary>
public static class Scanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,     // skip folders we don't have permission for
        RecurseSubdirectories = false, // we recurse ourselves so we can total sizes
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,          // include hidden/system files - they still use space
    };

    private sealed class State
    {
        public long Folders, Files, Bytes;
        public string Current = "";
        public long NextReport;
        public required IProgress<ScanProgress>? Progress;
        public required CancellationToken Token;

        public void Report(bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now < NextReport) return;
            NextReport = now + 200; // at most 5 updates per second
            Progress?.Report(new ScanProgress(Folders, Files, Bytes, Current));
        }
    }

    public static FolderNode Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        // "C:" on its own means "current dir on C", so keep the slash for drive roots.
        if (full.Length == 2 && full[1] == ':') full += Path.DirectorySeparatorChar;

        var root = new FolderNode(full, null) { IsExpanded = true };
        var state = new State { Progress = progress, Token = token };
        var dir = new DirectoryInfo(full);
        root.Modified = dir.LastWriteTime;

        ScanFolder(root, dir, state);
        state.Report(force: true);
        return root;
    }

    private static void ScanFolder(FolderNode node, DirectoryInfo dir, State s)
    {
        s.Token.ThrowIfCancellationRequested();

        var subFolders = new List<FolderNode>();
        long size = 0, files = 0;

        try
        {
            foreach (var info in dir.EnumerateFileSystemInfos("*", Options))
            {
                if (info is DirectoryInfo d)
                {
                    // Skip junctions and symbolic links (e.g. "Application Data") - following them
                    // would count the same data twice or loop forever. OneDrive folders are also
                    // reparse points, but they have no LinkTarget, so they're still scanned.
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0 && d.LinkTarget != null)
                        continue;

                    var child = new FolderNode(d.Name, node) { Modified = d.LastWriteTime };
                    ScanFolder(child, d, s);
                    subFolders.Add(child);
                    size += child.Size;
                    files += child.FileCount;
                }
                else if (info is FileInfo f)
                {
                    long length;
                    try { length = f.Length; } catch { length = 0; }

                    node.Files.Add(new FileNode(f.Name, node, length, f.LastWriteTime, f.Attributes));
                    size += length;
                    files++;
                    s.Files++;
                    s.Bytes += length;
                }
            }
        }
        catch (UnauthorizedAccessException) { node.AccessDenied = true; }
        catch (IOException) { node.AccessDenied = true; }

        // Largest first everywhere, so the tree and charts read naturally.
        subFolders.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var sub in subFolders) node.Folders.Add(sub);
        node.Files.Sort((a, b) => b.Size.CompareTo(a.Size));

        node.Size = size;
        node.FileCount = files;

        s.Folders++;
        s.Current = dir.FullName;
        s.Report();
    }
}
