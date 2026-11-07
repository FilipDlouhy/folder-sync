namespace FolderSync.Sync;

/// <summary>Turns source and replica snapshots into ordered operations; only the comparer touches the disk.</summary>
public sealed class SyncPlanner(IFileComparer comparer)
{
    /// <summary>Return deletes first, then folder creates, then file copies, so each step has what it needs.</summary>
    public IReadOnlyList<SyncOperation> Plan(
        string sourceRoot,
        Snapshot source,
        string replicaRoot,
        Snapshot replica,
        CancellationToken cancellationToken = default) =>
    [
        .. PlanDeletes(source, replica),
        .. PlanDirectoryCreates(source, replica),
        .. PlanFileCopies(sourceRoot, source, replicaRoot, replica, cancellationToken),
    ];

    private static IEnumerable<SyncOperation> PlanDeletes(Snapshot source, Snapshot replica)
    {
        var obsolete = replica.Entries.Values
            .Where(entry => entry.Kind == EntryKind.Link || !ExistsWithSameKind(source, entry))   // a replica link could lead writes outside the replica
            .Where(entry => !IsInsideUnreadableDirectory(source, entry.RelativePath))   // never delete what we could not read
            .ToList();

        var files = obsolete
            .Where(entry => entry.Kind == EntryKind.File)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => new SyncOperation(SyncAction.DeleteFile, entry.RelativePath));

        var links = obsolete
            .Where(entry => entry.Kind == EntryKind.Link)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => new SyncOperation(SyncAction.DeleteLink, entry.RelativePath));

        // deepest first, otherwise a recursive parent delete makes the child delete fail
        var directories = obsolete
            .Where(entry => entry.Kind == EntryKind.Directory)
            .OrderByDescending(entry => entry.RelativePath.Length)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => new SyncOperation(SyncAction.DeleteDirectory, entry.RelativePath));

        return files.Concat(links).Concat(directories);
    }

    private static IEnumerable<SyncOperation> PlanDirectoryCreates(Snapshot source, Snapshot replica) =>
        source.Entries.Values
            .Where(entry => entry.Kind == EntryKind.Directory && !ExistsWithSameKind(replica, entry))
            .OrderBy(entry => entry.RelativePath.Length)    // parents before children
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => new SyncOperation(SyncAction.CreateDirectory, entry.RelativePath));

    private IEnumerable<SyncOperation> PlanFileCopies(
        string sourceRoot,
        Snapshot source,
        string replicaRoot,
        Snapshot replica,
        CancellationToken cancellationToken)
    {
        var sourceFiles = source.Entries.Values
            .Where(entry => entry.Kind == EntryKind.File)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal);

        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();    // hashing many or big files can take long, Ctrl+C must not wait for all of it
            var replicaFile = replica.Entries.GetValueOrDefault(sourceFile.RelativePath);
            if (replicaFile is not { Kind: EntryKind.File })
            {
                yield return new SyncOperation(SyncAction.CopyFile, sourceFile.RelativePath);
            }
            else if (HasChanged(sourceRoot, sourceFile, replicaRoot, replicaFile))
            {
                yield return new SyncOperation(SyncAction.UpdateFile, sourceFile.RelativePath);
            }
        }
    }

    // size check is free, hashing only runs when sizes match
    private bool HasChanged(string sourceRoot, FileSystemEntry sourceFile, string replicaRoot, FileSystemEntry replicaFile) =>
        sourceFile.Size != replicaFile.Size
        || !comparer.AreEqual(
            Path.Combine(sourceRoot, sourceFile.RelativePath),
            Path.Combine(replicaRoot, replicaFile.RelativePath));

    private static bool ExistsWithSameKind(Snapshot snapshot, FileSystemEntry entry) =>
        snapshot.Entries.GetValueOrDefault(entry.RelativePath)?.Kind == entry.Kind;

    // trailing separator keeps "secret" from matching "secret2"
    private static bool IsInsideUnreadableDirectory(Snapshot source, string path) =>
        source.UnreadableDirectories.Any(directory =>
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
