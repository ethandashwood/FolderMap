using FolderMap.Models;

namespace FolderMap.Services;

public sealed record ScanProgress(long Folders, long Files, long Bytes, string Current);

/// <summary>Walks a folder and builds the in-memory tree with sizes.</summary>
public static class Scanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,     // skip folders we don't have permission for
        RecurseSubdirectories = false, // we walk the tree ourselves so we can total sizes
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

    /// <summary>
    /// Turn what the user typed into the folder to scan. "C:" on its own means "the current
    /// folder on drive C" to Windows, but people always mean the whole drive.
    /// </summary>
    public static string NormalizeRoot(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':' && char.IsLetter(path[0]))
            path += Path.DirectorySeparatorChar;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length == 2 && full[1] == ':') full += Path.DirectorySeparatorChar; // keep "C:\"
        return full;
    }

    public static FolderNode Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var full = NormalizeRoot(rootPath);
        var root = new FolderNode(full, null) { IsExpanded = true };
        var state = new State { Progress = progress, Token = token };
        var rootDir = new DirectoryInfo(full);
        root.Modified = rootDir.LastWriteTime;

        // Phase 1: read every folder, parents before children. A loop with our own stack
        // (instead of recursion) means extremely deep folder trees can't crash the app.
        var visitOrder = new List<(FolderNode Node, List<FolderNode> SubFolders)>();
        var pending = new Stack<(FolderNode Node, DirectoryInfo Dir)>();
        pending.Push((root, rootDir));

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, dir) = pending.Pop();
            var subFolders = ReadFolder(node, dir, state, pending);
            visitOrder.Add((node, subFolders));
        }

        // Phase 2: total sizes bottom-up. Children were visited after their parents,
        // so walking the list backwards always finishes children first.
        for (int i = visitOrder.Count - 1; i >= 0; i--)
        {
            var (node, subFolders) = visitOrder[i];
            long size = node.Files.Sum(f => f.Size);
            long files = node.Files.Count;
            foreach (var sub in subFolders)
            {
                size += sub.Size;
                files += sub.FileCount;
            }

            // Largest first everywhere, so the tree and charts read naturally.
            subFolders.Sort((a, b) => b.Size.CompareTo(a.Size));
            foreach (var sub in subFolders) node.Folders.Add(sub);
            node.Files.Sort((a, b) => b.Size.CompareTo(a.Size));

            node.Size = size;
            node.FileCount = files;
        }

        state.Report(force: true);
        return root;
    }

    private static List<FolderNode> ReadFolder(FolderNode node, DirectoryInfo dir, State s,
        Stack<(FolderNode, DirectoryInfo)> pending)
    {
        var subFolders = new List<FolderNode>();
        int seen = 0;
        try
        {
            foreach (var info in dir.EnumerateFileSystemInfos("*", Options))
            {
                // Check for Cancel inside big folders too, not just between folders.
                if (++seen % 1000 == 0) s.Token.ThrowIfCancellationRequested();

                if (info is DirectoryInfo d)
                {
                    // Skip junctions and symbolic links (e.g. "Application Data") - following them
                    // would count the same data twice or loop forever. OneDrive folders are also
                    // reparse points, but they have no LinkTarget, so they're still scanned.
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0 && d.LinkTarget != null)
                        continue;

                    var child = new FolderNode(d.Name, node) { Modified = d.LastWriteTime };
                    subFolders.Add(child);
                    pending.Push((child, d));
                }
                else if (info is FileInfo f)
                {
                    long length;
                    try { length = f.Length; } catch { length = 0; }

                    node.Files.Add(new FileNode(f.Name, node, length, f.LastWriteTime, f.Attributes));
                    s.Files++;
                    s.Bytes += length;
                }
            }
        }
        catch (UnauthorizedAccessException) { node.AccessDenied = true; }
        catch (IOException) { node.AccessDenied = true; }

        s.Folders++;
        s.Current = dir.FullName;
        s.Report();
        return subFolders;
    }
}
