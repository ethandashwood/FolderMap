using System.Security.Cryptography;
using FolderMap.Models;

namespace FolderMap.Services;

/// <summary>
/// Finds identical files in three passes so it stays fast:
///   1. group by exact size (free - we already know sizes),
///   2. hash the first 64 KB of same-size files,
///   3. hash the whole file only for files that still match.
/// </summary>
public static class DuplicateFinder
{
    private const int PartialBytes = 64 * 1024;

    public static List<List<FileNode>> Find(FolderNode root, long minSize, IProgress<string>? progress, CancellationToken token)
    {
        var sizeGroups = root.AllFiles()
            .Where(f => f.Size >= minSize && f.Size > 0 && !f.IsCloudOnly)
            .GroupBy(f => f.Size)
            .Select(g => g.ToList())
            .Where(g => g.Count > 1)
            .ToList();

        int total = sizeGroups.Sum(g => g.Count);
        int done = 0;
        long nextReport = 0;
        var result = new List<List<FileNode>>();

        foreach (var group in sizeGroups)
        {
            token.ThrowIfCancellationRequested();

            foreach (var partialMatch in GroupByHash(group, partial: true, token))
            {
                // Small files were fully read by the partial hash already.
                var matches = group[0].Size <= PartialBytes
                    ? new List<List<FileNode>> { partialMatch }
                    : GroupByHash(partialMatch, partial: false, token);

                result.AddRange(matches);
            }

            done += group.Count;
            if (Environment.TickCount64 >= nextReport)
            {
                nextReport = Environment.TickCount64 + 250;
                progress?.Report($"Checking for duplicates… {done:N0} of {total:N0} candidate files");
            }
        }

        // Biggest wasted space first.
        return result.OrderByDescending(g => g[0].Size * (g.Count - 1)).ToList();
    }

    private static List<List<FileNode>> GroupByHash(IEnumerable<FileNode> files, bool partial, CancellationToken token)
    {
        var byHash = new Dictionary<string, List<FileNode>>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var hash = Hash(file.FullPath, partial);
            if (hash is null) continue; // unreadable - skip

            if (!byHash.TryGetValue(hash, out var list))
                byHash[hash] = list = new List<FileNode>();
            list.Add(file);
        }
        return byHash.Values.Where(l => l.Count > 1).ToList();
    }

    private static string? Hash(string path, bool partial)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

            if (partial)
            {
                var buffer = new byte[PartialBytes];
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
            }
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }
}
