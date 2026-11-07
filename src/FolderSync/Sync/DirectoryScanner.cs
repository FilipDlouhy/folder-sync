using Serilog;

namespace FolderSync.Sync;

/// <summary>Walks a folder tree iteratively and returns a flat snapshot keyed by relative path.</summary>
public sealed class DirectoryScanner(ILogger logger)
{
    private static readonly EnumerationOptions DirectChildren = new()
    {
        AttributesToSkip = 0,          // include hidden and system files
        IgnoreInaccessible = false,    // surface access errors so the folder can be protected
    };

    public Snapshot Scan(string root)
    {
        var rootDirectory = new DirectoryInfo(root);
        var entries = new Dictionary<string, FileSystemEntry>();
        var unreadableDirectories = new HashSet<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            // ---- read direct children of the next folder
            var directory = pending.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = directory.EnumerateFileSystemInfos("*", DirectChildren).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && !ReferenceEquals(directory, rootDirectory))
            {
                // unreadable subfolder is remembered so the planner keeps its replica copy; root errors propagate
                var relativePath = Path.GetRelativePath(rootDirectory.FullName, directory.FullName);
                unreadableDirectories.Add(relativePath);
                logger.Warning("Cannot read folder {Path} in {Root}: {Reason}", relativePath, rootDirectory.FullName, ex.Message);
                continue;
            }

            // ---- record files and folders, queue subfolders
            foreach (var child in children)
            {
                var relativePath = Path.GetRelativePath(rootDirectory.FullName, child.FullName);

                // links are recorded but never followed; the planner removes them from the replica
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint) && child.LinkTarget is not null)
                {
                    logger.Warning("Not following symbolic link {Path}", relativePath);
                    entries.Add(relativePath, FileSystemEntry.ForLink(relativePath));
                    continue;
                }

                if (child is DirectoryInfo subdirectory)
                {
                    entries.Add(relativePath, FileSystemEntry.ForDirectory(relativePath));
                    pending.Push(subdirectory);
                }
                else if (child is FileInfo file)
                {
                    entries.Add(relativePath, FileSystemEntry.ForFile(relativePath, file.Length));
                }
            }
        }

        return new Snapshot(entries, unreadableDirectories);
    }
}
