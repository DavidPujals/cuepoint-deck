using System.Text;

namespace CuepointDeck.Core.Storage;

/// <summary>Identity of a file's on-disk version: last write time and length. Used to notice edits from another PC.</summary>
public readonly record struct FileStamp(long LastWriteUtcTicks, long Length)
{
    public static FileStamp Of(string path)
    {
        var fi = new FileInfo(path);
        return new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length);
    }

    public static FileStamp? TryOf(string path)
    {
        try { return File.Exists(path) ? Of(path) : null; }
        catch { return null; }
    }
}

/// <summary>Atomic-ish writes for a library that may live on a network share and be edited from two PCs:
/// write a temp file in the same folder, then swap it over the original, keeping the previous version as .bak.</summary>
public static class SafeFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static FileStamp WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
        var bak = path + ".bak";
        try
        {
            File.WriteAllText(tmp, content, Utf8NoBom);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
                {
                    // Some shares refuse ReplaceFile. Fall back to copy-then-move; still never leaves a half-written original.
                    File.Copy(path, bak, overwrite: true);
                    File.Move(tmp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
            return FileStamp.Of(path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Deletes a file but leaves a .bak copy of it behind.</summary>
    public static void DeleteKeepingBackup(string path)
    {
        if (!File.Exists(path)) return;
        File.Copy(path, path + ".bak", overwrite: true);
        File.Delete(path);
    }
}
