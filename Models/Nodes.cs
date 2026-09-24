using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderMap.Services;

namespace FolderMap.Models;

/// <summary>Base class for anything in the scanned tree (a folder or a file).</summary>
public abstract class FsNode : ObservableObject
{
    private string _name;
    private long _size;

    protected FsNode(string name, FolderNode? parent)
    {
        _name = name;
        Parent = parent;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(FullPath));
            }
        }
    }

    public FolderNode? Parent { get; internal set; }

    /// <summary>Size in bytes. For folders this is the total of everything inside.</summary>
    public long Size
    {
        get => _size;
        set
        {
            if (SetProperty(ref _size, value))
            {
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(PercentOfParent));
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public DateTime Modified { get; set; }

    public abstract bool IsFolder { get; }
    public abstract string Kind { get; }
    public abstract string FilesText { get; }

    // The full path is worked out from the parent chain, so renaming a folder
    // automatically "moves" everything under it without touching each node.
    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);
    public string ParentPath => Parent?.FullPath ?? "";

    public string SizeText => Format.Bytes(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    public double PercentOfParent =>
        Parent is null || Parent.Size <= 0 ? 100 : Size * 100.0 / Parent.Size;

    public string PercentText => $"{PercentOfParent:0.0}%";

    public bool IsUnder(FolderNode folder)
    {
        for (var p = Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, folder)) return true;
        return false;
    }
}

public sealed class FileNode : FsNode
{
    // Windows cloud-file attributes (OneDrive "online-only" files). Reading these
    // would force a download, so the duplicate finder skips them.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public FileNode(string name, FolderNode parent, long size, DateTime modified, FileAttributes attributes)
        : base(name, parent)
    {
        Size = size;
        Modified = modified;
        Attributes = attributes;
    }

    public FileAttributes Attributes { get; }
    public override bool IsFolder => false;

    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
    public override string Kind => Extension.Length > 0 ? Extension : "file";
    public override string FilesText => "";

    public bool IsCloudOnly =>
        (Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
}

public sealed class FolderNode : FsNode
{
    private long _fileCount;
    private bool _isExpanded;

    public FolderNode(string name, FolderNode? parent) : base(name, parent) { }

    /// <summary>Sub-folders, kept sorted largest first. Observable so the tree view updates.</summary>
    public ObservableCollection<FolderNode> Folders { get; } = new();

    /// <summary>Files directly inside this folder, largest first.</summary>
    public List<FileNode> Files { get; } = new();

    /// <summary>Total number of files in this folder and everything below it.</summary>
    public long FileCount
    {
        get => _fileCount;
        set
        {
            if (SetProperty(ref _fileCount, value)) OnPropertyChanged(nameof(FilesText));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool AccessDenied { get; set; }

    public override bool IsFolder => true;
    public override string Kind => "Folder";
    public override string FilesText => FileCount.ToString("N0");

    public IEnumerable<FsNode> Children => Folders.Cast<FsNode>().Concat(Files);

    /// <summary>Every file below this folder (non-recursive walk, safe for very deep trees).</summary>
    public IEnumerable<FileNode> AllFiles()
    {
        var stack = new Stack<FolderNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            foreach (var file in f.Files) yield return file;
            foreach (var sub in f.Folders) stack.Push(sub);
        }
    }

    /// <summary>Every node below this folder; folders included only if asked.</summary>
    public IEnumerable<FsNode> Descendants(bool includeFolders)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            foreach (var file in f.Files) yield return file;
            foreach (var sub in f.Folders)
            {
                if (includeFolders) yield return sub;
                stack.Push(sub);
            }
        }
    }

    /// <summary>Find a scanned folder by its full path, or null if it isn't in this tree.</summary>
    public FolderNode? FindFolder(string path)
    {
        var rootPath = FullPath;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(target, Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase))
            return this;

        var relative = Path.GetRelativePath(rootPath, target);
        if (relative.StartsWith("..") || Path.IsPathRooted(relative)) return null;

        var current = this;
        foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Folders.FirstOrDefault(f =>
                string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    /// <summary>Detach a child and subtract its size / file count from every ancestor.</summary>
    public void Remove(FsNode child)
    {
        long size = child.Size;
        long files = child is FolderNode fn ? fn.FileCount : 1;

        if (child is FolderNode folder) Folders.Remove(folder);
        else Files.Remove((FileNode)child);

        for (var p = this; p != null; p = p.Parent)
        {
            p.Size -= size;
            p.FileCount -= files;
        }
        child.Parent = null;
    }

    /// <summary>Attach a child (e.g. after a move) and add its size to every ancestor.</summary>
    public void Add(FsNode child)
    {
        child.Parent = this;
        if (child is FolderNode folder)
        {
            int i = 0;
            while (i < Folders.Count && Folders[i].Size >= folder.Size) i++;
            Folders.Insert(i, folder);
        }
        else
        {
            var file = (FileNode)child;
            int i = 0;
            while (i < Files.Count && Files[i].Size >= file.Size) i++;
            Files.Insert(i, file);
        }

        long files = child is FolderNode fn ? fn.FileCount : 1;
        for (var p = this; p != null; p = p.Parent)
        {
            p.Size += child.Size;
            p.FileCount += files;
        }
    }
}

/// <summary>One row in the Duplicates tab.</summary>
public sealed class DuplicateRow
{
    public DuplicateRow(int group, FileNode file)
    {
        Group = group;
        File = file;
    }

    public int Group { get; }
    public FileNode File { get; }
    public string Name => File.Name;
    public long Size => File.Size;
    public string SizeText => File.SizeText;
    public string Folder => File.ParentPath;
    public string ModifiedText => File.ModifiedText;
    public DateTime Modified => File.Modified;
}

/// <summary>One row in the File types tab.</summary>
public sealed class TypeSummary
{
    public required string Extension { get; init; }
    public long Count { get; init; }
    public long TotalSize { get; init; }
    public double Percent { get; init; }

    public string CountText => Count.ToString("N0");
    public string SizeText => Format.Bytes(TotalSize);
    public string PercentText => $"{Percent:0.0}%";
}
