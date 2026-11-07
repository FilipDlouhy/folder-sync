using System.CommandLine;

namespace FolderSync.Cli;

/// <summary>Validated command-line settings with absolute paths.</summary>
public sealed record SyncOptions(string Source, string Replica, TimeSpan Interval, string LogFile);

/// <summary>Defines the CLI options and validates them before the sync starts.</summary>
public static class CommandLine
{
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);    // PeriodicTimer caps at ~49 days

    private static readonly StringComparison PathComparison =             // Windows paths are case-insensitive
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static RootCommand Create(Func<SyncOptions, CancellationToken, Task<int>> run)
    {
        // ---- options
        var source = new Option<DirectoryInfo>("--source")
        {
            Description = "Folder to synchronize from. Must exist.",
            Required = true,
        };

        var replica = new Option<DirectoryInfo>("--replica")
        {
            Description = "Folder kept identical to the source. Created if missing.",
            Required = true,
        };

        var interval = new Option<int>("--interval")
        {
            Description = $"Seconds between synchronizations (1-{MaxInterval.TotalSeconds}).",
            Required = true,
        };

        var log = new Option<FileInfo>("--log")
        {
            Description = "Log file path. Created if missing, appended otherwise.",
            Required = true,
        };

        var command = new RootCommand("Keeps a replica folder identical to a source folder (one-way, periodic).")
        {
            source,
            replica,
            interval,
            log,
        };

        // ---- validate and run
        command.SetAction((parseResult, cancellationToken) =>
        {
            var options = new SyncOptions(
                parseResult.GetRequiredValue(source).FullName,
                parseResult.GetRequiredValue(replica).FullName,
                TimeSpan.FromSeconds(parseResult.GetRequiredValue(interval)),
                parseResult.GetRequiredValue(log).FullName);

            var errors = Validate(options);    // after parsing, so values are always typed
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    parseResult.InvocationConfiguration.Error.WriteLine(error);
                }

                return Task.FromResult(1);
            }

            return run(options, cancellationToken);
        });

        return command;
    }

    /// <summary>Return all problems at once so the user can fix them in one go.</summary>
    public static IReadOnlyList<string> Validate(SyncOptions options)
    {
        var errors = new List<string>();
        if (!Directory.Exists(options.Source))
        {
            errors.Add($"Source folder '{options.Source}' does not exist.");
        }

        if (File.Exists(options.Replica))
        {
            errors.Add($"Replica path '{options.Replica}' is a file, expected a folder.");
        }

        if (options.Interval < TimeSpan.FromSeconds(1) || options.Interval > MaxInterval)
        {
            errors.Add($"Interval must be between 1 and {MaxInterval.TotalSeconds} seconds.");
        }

        if (IsSameOrInside(options.Replica, options.Source) || IsSameOrInside(options.Source, options.Replica))
        {
            errors.Add("Source and replica must be different folders, neither inside the other.");
        }

        if (IsSameOrInside(options.LogFile, options.Source) || IsSameOrInside(options.LogFile, options.Replica))
        {
            errors.Add("Log file must not be inside the source or replica folder.");
        }

        if (Directory.Exists(options.LogFile))
        {
            errors.Add($"Log file path '{options.LogFile}' is a folder, expected a file.");
        }

        return errors;
    }

    // links resolved first, a junction must not hide an overlap; trailing separator keeps "data" from matching "data2"
    private static bool IsSameOrInside(string path, string folder)
    {
        var realPath = ResolveLinks(path).TrimEnd(Path.DirectorySeparatorChar);
        var realFolder = ResolveLinks(folder).TrimEnd(Path.DirectorySeparatorChar);
        return realPath.Equals(realFolder, PathComparison)
            || realPath.StartsWith(realFolder + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Replace every symbolic link or junction in the path, including its parent folders, by its target.</summary>
    private static string ResolveLinks(string path, int depth = 0)
    {
        const int MaxDepth = 40;    // stops link cycles
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var resolved = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var target = LinkTargetOrNull(resolved);
            if (target is not null && depth < MaxDepth)
            {
                // relative targets are relative to the link's folder
                resolved = ResolveLinks(Path.Combine(Path.GetDirectoryName(resolved)!, target), depth + 1);
            }
        }

        return resolved;
    }

    private static string? LinkTargetOrNull(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget;    // null when the path is missing or not a link
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
