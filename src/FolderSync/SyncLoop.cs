using FolderSync.Sync;
using Serilog;

namespace FolderSync;

/// <summary>Repeats sync cycles at a fixed interval until cancelled.</summary>
public sealed class SyncLoop(Synchronizer synchronizer, TimeSpan interval, ILogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            // first cycle runs immediately, then on every tick
            do
            {
                RunCycle(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Information("Synchronization stopped");
        }
    }

    private void RunCycle(CancellationToken cancellationToken)
    {
        try
        {
            synchronizer.RunOnce(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unexpected error ends only this cycle, the loop keeps running
            logger.Error(ex, "Sync cycle failed, will retry in {Interval} s", interval.TotalSeconds);
        }
    }
}
