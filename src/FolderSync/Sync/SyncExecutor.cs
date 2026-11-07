using Serilog;

namespace FolderSync.Sync;

/// <summary>Applies planned operations to the replica and logs each one; a failed item does not stop the rest.</summary>
public sealed class SyncExecutor(ILogger logger)
{
    public SyncSummary Execute(
        IReadOnlyList<SyncOperation> operations,
        string sourceRoot,
        string replicaRoot,
        CancellationToken cancellationToken)
    {
        var succeeded = new Dictionary<SyncAction, int>();
        var failed = 0;
        var undeletedLinks = new List<string>();    // anything written below them would land outside the replica

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();    // Ctrl+C stops between files, never mid-copy
            var blockingLink = undeletedLinks.Find(link => IsSameOrInside(operation.RelativePath, link));
            if (blockingLink is not null)
            {
                failed++;
                logger.Error("Skipped {Action} {Path}: link {Link} could not be removed", operation.Action.ToString(), operation.RelativePath, blockingLink);
                continue;
            }

            try
            {
                Apply(operation, sourceRoot, replicaRoot);
                succeeded[operation.Action] = succeeded.GetValueOrDefault(operation.Action) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // per-item failure, the next cycle retries it
                failed++;
                logger.Error("Failed to {Action} {Path}: {Reason}", operation.Action.ToString(), operation.RelativePath, ex.Message);    // string, an enum would be logged in quotes
                if (operation.Action == SyncAction.DeleteLink)
                {
                    undeletedLinks.Add(operation.RelativePath);
                }
            }
        }

        return new SyncSummary(
            Created: succeeded.GetValueOrDefault(SyncAction.CreateDirectory),
            Copied: succeeded.GetValueOrDefault(SyncAction.CopyFile),
            Updated: succeeded.GetValueOrDefault(SyncAction.UpdateFile),
            Deleted: succeeded.GetValueOrDefault(SyncAction.DeleteFile)
                + succeeded.GetValueOrDefault(SyncAction.DeleteDirectory)
                + succeeded.GetValueOrDefault(SyncAction.DeleteLink),
            Failed: failed);
    }

    private void Apply(SyncOperation operation, string sourceRoot, string replicaRoot)
    {
        var sourcePath = Path.Combine(sourceRoot, operation.RelativePath);
        var replicaPath = Path.Combine(replicaRoot, operation.RelativePath);

        switch (operation.Action)
        {
            case SyncAction.DeleteFile:
                ClearAttributes(replicaPath);
                File.Delete(replicaPath);
                logger.Information("Deleted file {Path}", operation.RelativePath);
                break;
            case SyncAction.DeleteDirectory:
                ClearAttributes(replicaPath);
                Directory.Delete(replicaPath, recursive: true);
                logger.Information("Deleted directory {Path}", operation.RelativePath);
                break;
            case SyncAction.DeleteLink:
                DeleteLink(replicaPath);
                logger.Information("Deleted link {Path}", operation.RelativePath);
                break;
            case SyncAction.CreateDirectory:
                Directory.CreateDirectory(replicaPath);
                logger.Information("Created directory {Path}", operation.RelativePath);
                break;
            case SyncAction.CopyFile:
                CopyFromSource(sourcePath, replicaPath);
                logger.Information("Copied new file {Path}", operation.RelativePath);
                break;
            case SyncAction.UpdateFile:
                CopyFromSource(sourcePath, replicaPath);
                logger.Information("Updated file {Path}", operation.RelativePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Action, "Unknown sync action.");
        }
    }

    private static void CopyFromSource(string sourcePath, string replicaPath)
    {
        ClearAttributes(replicaPath);
        File.Copy(sourcePath, replicaPath, overwrite: true);
    }

    // removes only the link itself, never its target; Windows needs Directory.Delete for folder links
    private static void DeleteLink(string path)
    {
        if (OperatingSystem.IsWindows() && File.GetAttributes(path).HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    // read-only or hidden entries make File.Copy, File.Delete and Directory.Delete fail on Windows
    private static void ClearAttributes(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        else if (OperatingSystem.IsWindows() && Directory.Exists(path))
        {
            // on Linux deleting a folder depends on its parent's permissions, not on the folder
            var directory = new DirectoryInfo(path);
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    // trailing separator keeps "docs" from matching "docs2"
    private static bool IsSameOrInside(string path, string folder) =>
        path == folder || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
