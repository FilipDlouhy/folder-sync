namespace FolderSync.Sync;

public enum EntryKind
{
    File,
    Directory,
    Link,
}

/// <summary>One file, folder or link, identified by its path relative to the scanned root; only files have a size.</summary>
public sealed record FileSystemEntry(string RelativePath, EntryKind Kind, long Size)
{
    public static FileSystemEntry ForFile(string relativePath, long size) =>
        new(relativePath, EntryKind.File, size);

    public static FileSystemEntry ForDirectory(string relativePath) =>
        new(relativePath, EntryKind.Directory, 0);

    public static FileSystemEntry ForLink(string relativePath) =>
        new(relativePath, EntryKind.Link, 0);
}

/// <summary>Result of one folder scan: all entries plus subfolders that could not be read.</summary>
public sealed record Snapshot(
    IReadOnlyDictionary<string, FileSystemEntry> Entries,
    IReadOnlySet<string> UnreadableDirectories);
