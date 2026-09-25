namespace FolderMap.Tests;

/// <summary>A throwaway folder for a test. Deleted (permanently) when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderMapTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>Create a file of the given size filled with a repeating byte.</summary>
    public string File(string relative, int size, byte fill = 1)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        return File(relative, bytes);
    }

    public string File(string relative, byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public string Text(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Folder(string relative)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort */ }
    }
}
