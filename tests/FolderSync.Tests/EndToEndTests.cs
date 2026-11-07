using System.Diagnostics;
using System.Text;
using FolderSync.Tests.TestSupport;

namespace FolderSync.Tests;

/// <summary>Runs the real app as a separate process.</summary>
public sealed class EndToEndTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);    // upper bound only, tests finish as soon as the log shows the line

    private TempDirectory _temp = null!;
    private string _source = null!;
    private string _replica = null!;
    private string _log = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = new TempDirectory();
        _source = _temp.PathOf("source");
        _replica = _temp.PathOf("replica");
        _log = _temp.PathOf(Path.Combine("logs", "sync.log"));
        Directory.CreateDirectory(_source);
    }

    [TearDown]
    public void TearDown() => _temp.Dispose();

    [Test]
    public void App_SyncsPeriodicallyAndLogsToConsoleAndFile()
    {
        _temp.WriteFile("source/first.txt", "1");

        using var app = RunningApp.Start("--source", _source, "--replica", _replica, "--interval", "1", "--log", _log);
        WaitUntil(() => ReadShared(_log).Contains("Copied new file first.txt"), "first file to be synced");

        _temp.WriteFile("source/second.txt", "2");
        WaitUntil(() => ReadShared(_log).Contains("Copied new file second.txt"), "second file to be synced by a later cycle");
        app.Stop();    // kill is fine, the log is written unbuffered

        Assert.That(File.ReadAllText(Path.Combine(_replica, "second.txt")), Is.EqualTo("2"));
        Assert.That(app.Output, Does.Contain("Copied new file first.txt").And.Contain("Copied new file second.txt"));
    }

    [Test]
    public void App_InvalidArguments_ExitsWithErrorAndNoLog()
    {
        using var app = RunningApp.Start("--source", _temp.PathOf("missing"), "--replica", _replica, "--interval", "1", "--log", _log);

        var exitCode = app.WaitForExit(WaitLimit);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(app.Error, Does.Contain("does not exist"));
        Assert.That(File.Exists(_log), Is.False);
    }

    [Test]
    public void App_UnwritableLogFile_ExitsWithError()
    {
        var blocker = _temp.WriteFile("blocker.txt", "a file where the log folder should be");
        var log = Path.Combine(blocker, "sync.log");

        using var app = RunningApp.Start("--source", _source, "--replica", _replica, "--interval", "1", "--log", log);

        var exitCode = app.WaitForExit(WaitLimit);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(app.Error, Does.Contain("Cannot write log file"));
        Assert.That(Directory.Exists(_replica), Is.False);
    }

    [Test]
    public void App_StopSignalDuringFirstSync_FinishesCurrentFileAndExitsWithZero([Values("INT", "TERM")] string signal)
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "POSIX signals are sent with kill, Ctrl+C on Windows was verified manually.");
        const long bigSize = 200L * 1024 * 1024;    // big enough that the signal lands while the first sync is still running
        WriteZeros(_temp.PathOf("source/big.bin"), bigSize);
        _temp.WriteFile("source/z.txt", "copied after big.bin");

        using var app = RunningApp.Start("--source", _source, "--replica", _replica, "--interval", "60", "--log", _log);
        WaitUntil(() => ReadShared(_log).Contains("Starting sync"), "the app to start");
        using (var kill = Process.Start("kill", ["-" + signal, app.Id.ToString()])!)
        {
            kill.WaitForExit();
        }

        var exitCode = app.WaitForExit(WaitLimit);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(ReadShared(_log), Does.Contain("Synchronization stopped"));
        var copy = new FileInfo(Path.Combine(_replica, "big.bin"));
        Assert.That(!copy.Exists || copy.Length == bigSize, Is.True, "big.bin must be either not started or complete, never partial.");
    }

    private static void WriteZeros(string path, long size)
    {
        var buffer = new byte[1024 * 1024];
        using var stream = File.Create(path);
        for (long written = 0; written < size; written += buffer.Length)
        {
            stream.Write(buffer);
        }
    }

    // poll instead of fixed sleeps, fast when the app is fast
    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > WaitLimit)
            {
                Assert.Fail($"Timed out waiting for {description}.");
            }

            Thread.Sleep(50);
        }
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);    // the app still holds the log open
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Started app process with captured stdout and stderr; killed on dispose.</summary>
    private sealed class RunningApp : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _error = new();

        private RunningApp(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => Append(_output, e.Data);
            _process.ErrorDataReceived += (_, e) => Append(_error, e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Output => Read(_output);

        public string Error => Read(_error);

        public int Id => _process.Id;

        public static RunningApp Start(params string[] arguments)
        {
            // same dotnet host that runs the tests, plain "dotnet" from PATH as fallback
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FolderSync.dll"));
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return new RunningApp(Process.Start(startInfo)!);
        }

        public int WaitForExit(TimeSpan timeout)
        {
            Assert.That(_process.WaitForExit(timeout), Is.True, "App did not exit in time.");
            _process.WaitForExit();
            return _process.ExitCode;
        }

        public void Stop()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.WaitForExit();
        }

        public void Dispose()
        {
            Stop();
            _process.Dispose();
        }

        private static void Append(StringBuilder buffer, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (buffer)
            {
                buffer.AppendLine(line);
            }
        }

        private static string Read(StringBuilder buffer)
        {
            lock (buffer)
            {
                return buffer.ToString();
            }
        }
    }
}
