using System.Diagnostics;
using Serilog;

namespace FolderSync.Sync;

/// <summary>Runs one sync cycle: scan both folders, plan the changes, apply them.</summary>
public sealed class Synchronizer(
    string sourceRoot,
    string replicaRoot,
    DirectoryScanner scanner,
    SyncPlanner planner,
    SyncExecutor executor,
    ILogger logger)
{
    private int _cycle;

    /// <summary>Return the cycle summary, or null when the source is missing and the cycle was skipped.</summary>
    public SyncSummary? RunOnce(CancellationToken cancellationToken)
    {
        var cycle = ++_cycle;
        if (!Directory.Exists(sourceRoot))
        {
            // missing source (e.g. unplugged drive) must not wipe the replica
            logger.Error("Sync #{Cycle} skipped: source folder {Source} is missing, replica left untouched", cycle, sourceRoot);
            return null;
        }

        // ---- scan, plan, apply
        var stopwatch = Stopwatch.StartNew();
        if (!Directory.Exists(replicaRoot))
        {
            Directory.CreateDirectory(replicaRoot);
            logger.Information("Created replica folder {Replica}", replicaRoot);
        }

        var source = scanner.Scan(sourceRoot);
        var replica = scanner.Scan(replicaRoot);
        var operations = planner.Plan(sourceRoot, source, replicaRoot, replica, cancellationToken);
        var summary = executor.Execute(operations, sourceRoot, replicaRoot, cancellationToken);

        // content of an unreadable replica folder is unknown, so the replica cannot be confirmed to match the source
        foreach (var directory in replica.UnreadableDirectories)
        {
            logger.Error("Cannot read replica folder {Path}, it may contain files that are not in the source", directory);
        }

        summary = summary with { Failed = summary.Failed + replica.UnreadableDirectories.Count };

        logger.Information(
            "Sync #{Cycle} done in {ElapsedMs} ms: copied {Copied}, updated {Updated}, deleted {Deleted}, new folders {Created}, failed {Failed}",
            cycle,
            stopwatch.ElapsedMilliseconds,
            summary.Copied,
            summary.Updated,
            summary.Deleted,
            summary.Created,
            summary.Failed);

        return summary;
    }
}
