using System.CommandLine;
using FolderSync.Cli;
using FolderSync.Tests.TestSupport;

namespace FolderSync.Tests;

/// <summary>Validation rules and argument parsing, without starting a sync.</summary>
public sealed class CommandLineTests
{
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
        _log = _temp.PathOf("sync.log");
        Directory.CreateDirectory(_source);
    }

    [TearDown]
    public void TearDown() => _temp.Dispose();

    [Test]
    public void Validate_ValidOptions_ReturnsNoErrors()
    {
        Assert.That(CommandLine.Validate(Options()), Is.Empty);
    }

    [Test]
    public void Validate_MissingSource_ReturnsError()
    {
        AssertSingleError(Options(source: _temp.PathOf("missing")), "does not exist");
    }

    [Test]
    public void Validate_ReplicaIsFile_ReturnsError()
    {
        var replicaFile = _temp.WriteFile("replica.txt", "x");

        AssertSingleError(Options(replica: replicaFile), "is a file");
    }

    [TestCase(0)]
    [TestCase(86_401)]
    public void Validate_IntervalOutOfRange_ReturnsError(int seconds)
    {
        AssertSingleError(Options(intervalSeconds: seconds), "Interval must be between");
    }

    [TestCase(1)]
    [TestCase(86_400)]
    public void Validate_IntervalAtBoundary_IsAccepted(int seconds)
    {
        Assert.That(CommandLine.Validate(Options(intervalSeconds: seconds)), Is.Empty);
    }

    [Test]
    public void Validate_ReplicaSameAsSource_ReturnsError()
    {
        AssertSingleError(Options(replica: _source), "must be different folders");
    }

    [Test]
    public void Validate_ReplicaInsideSource_ReturnsError()
    {
        AssertSingleError(Options(replica: Path.Combine(_source, "replica")), "must be different folders");
    }

    [Test]
    public void Validate_SourceInsideReplica_ReturnsError()
    {
        var nestedSource = Path.Combine(_replica, "source");
        Directory.CreateDirectory(nestedSource);

        AssertSingleError(Options(source: nestedSource), "must be different folders");
    }

    [Test]
    public void Validate_ReplicaIsLinkToFolderAboveSource_ReturnsError()
    {
        // syncing into "replica" would see the source as extra content and delete it
        var nestedSource = _temp.PathOf("outer/source");
        Directory.CreateDirectory(nestedSource);
        Links.CreateDirectoryLink(_replica, _temp.PathOf("outer"));

        AssertSingleError(Options(source: nestedSource), "must be different folders");
    }

    [Test]
    public void Validate_SourcePathGoesThroughLinkIntoReplica_ReturnsError()
    {
        Directory.CreateDirectory(Path.Combine(_replica, "inner"));
        Links.CreateDirectoryLink(_temp.PathOf("shortcut"), _replica);

        AssertSingleError(Options(source: _temp.PathOf("shortcut/inner")), "must be different folders");
    }

    [Test]
    public void Validate_LinkToUnrelatedFolder_IsAccepted()
    {
        Directory.CreateDirectory(_temp.PathOf("elsewhere"));
        Links.CreateDirectoryLink(_replica, _temp.PathOf("elsewhere"));

        Assert.That(CommandLine.Validate(Options()), Is.Empty);
    }

    [Test]
    public void Validate_SiblingFolderWithSamePrefix_IsAccepted()
    {
        Assert.That(CommandLine.Validate(Options(replica: _source + "2")), Is.Empty);
    }

    [Test]
    public void Validate_ReplicaDiffersOnlyByCaseOnWindows_ReturnsError()
    {
        Assume.That(OperatingSystem.IsWindows(), "Paths are case-sensitive outside Windows.");

        AssertSingleError(Options(replica: _source.ToUpperInvariant()), "must be different folders");
    }

    [TestCase("source")]
    [TestCase("replica")]
    public void Validate_LogInsideSyncedFolder_ReturnsError(string folder)
    {
        AssertSingleError(Options(log: _temp.PathOf(Path.Combine(folder, "sync.log"))), "Log file must not be inside");
    }

    [Test]
    public void Validate_LogIsFolder_ReturnsError()
    {
        var logFolder = _temp.PathOf("logs");
        Directory.CreateDirectory(logFolder);

        AssertSingleError(Options(log: logFolder), "is a folder");
    }

    [Test]
    public async Task Invoke_ValidArguments_RunsWithResolvedOptions()
    {
        var result = await InvokeAsync("--source", _source, "--replica", _replica, "--interval", "5", "--log", _log);

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Received, Is.EqualTo(Options(intervalSeconds: 5)));
    }

    [Test]
    public async Task Invoke_MissingRequiredOption_FailsWithoutRunning()
    {
        var result = await InvokeAsync("--source", _source, "--replica", _replica, "--interval", "5");

        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Received, Is.Null);
        Assert.That(result.Error, Does.Contain("--log"));
    }

    [Test]
    public async Task Invoke_NonNumericInterval_FailsWithoutRunning()
    {
        var result = await InvokeAsync("--source", _source, "--replica", _replica, "--interval", "abc", "--log", _log);

        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Received, Is.Null);
    }

    [Test]
    public async Task Invoke_InvalidValues_PrintsAllErrorsAndReturnsOne()
    {
        var result = await InvokeAsync("--source", _temp.PathOf("missing"), "--replica", _replica, "--interval", "0", "--log", _log);

        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Received, Is.Null);
        Assert.That(result.Error, Does.Contain("does not exist").And.Contain("Interval must be between"));
    }

    private SyncOptions Options(string? source = null, string? replica = null, int intervalSeconds = 10, string? log = null) =>
        new(source ?? _source, replica ?? _replica, TimeSpan.FromSeconds(intervalSeconds), log ?? _log);

    private static void AssertSingleError(SyncOptions options, string expectedFragment)
    {
        var errors = CommandLine.Validate(options);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(expectedFragment));
    }

    private static async Task<(int ExitCode, string Error, SyncOptions? Received)> InvokeAsync(params string[] args)
    {
        // fake run only records what it received
        SyncOptions? received = null;
        var command = CommandLine.Create((options, _) =>
        {
            received = options;
            return Task.FromResult(0);
        });

        var error = new StringWriter();
        var configuration = new InvocationConfiguration { Output = new StringWriter(), Error = error };
        var exitCode = await command.Parse(args).InvokeAsync(configuration);

        return (exitCode, error.ToString(), received);
    }
}
