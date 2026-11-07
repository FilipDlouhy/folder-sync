// Entry point: parse arguments, set up logging and run the sync loop until Ctrl+C.
using System.CommandLine;
using FolderSync;
using FolderSync.Cli;
using FolderSync.Sync;
using Serilog;

const string LogTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";   // same for console and file

// Ctrl+C only cancels the token, the current file finishes instead of a forced kill after 2 s
var invocation = new InvocationConfiguration { ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };
return await CommandLine.Create(RunAsync).Parse(args).InvokeAsync(invocation);

static async Task<int> RunAsync(SyncOptions options, CancellationToken cancellationToken)
{
    // System.CommandLine starts tracking this task only after its first await; without the yield
    // a Ctrl+C during the first sync would not wait for it and would end the process mid-copy
    await Task.Yield();

    // ---- fail fast if the log file cannot be written, Serilog would only drop the lines silently
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.LogFile)!);
        using (File.Open(options.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Cannot write log file '{options.LogFile}': {ex.Message}");
        return 1;
    }

    // ---- logging (await using flushes the file on exit)
    await using var logger = new LoggerConfiguration()
        .WriteTo.Console(outputTemplate: LogTemplate)
        .WriteTo.File(options.LogFile, outputTemplate: LogTemplate)
        .CreateLogger();

    logger.Information(
        "Starting sync {Source} -> {Replica} every {Interval} s, log file {LogFile}. Press Ctrl+C to stop.",
        options.Source,
        options.Replica,
        options.Interval.TotalSeconds,
        options.LogFile);

    // ---- wire up and run
    var synchronizer = new Synchronizer(
        options.Source,
        options.Replica,
        new DirectoryScanner(logger),
        new SyncPlanner(new Md5FileComparer()),
        new SyncExecutor(logger),
        logger);

    await new SyncLoop(synchronizer, options.Interval, logger).RunAsync(cancellationToken);
    return 0;
}
