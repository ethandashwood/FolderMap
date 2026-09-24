namespace FolderMap.Services;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }
}
