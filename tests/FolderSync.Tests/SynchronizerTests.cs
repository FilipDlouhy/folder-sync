using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FolderSync.Sync;
using FolderSync.Tests.TestSupport;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FolderSync.Tests;

/// <summary>Integration tests on real folders, one sync cycle at a time.</summary>
public sealed class SynchronizerTests
{
    private static readonly EnumerationOptions AllEntries = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
    };

    private TempDirectory _temp = null!;
    private string _source = null!;
    private string _replica = null!;
    private CollectingSink _sink = null!;
    private Logger _logger = null!;
    private Synchronizer _synchronizer = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = new TempDirectory();
        _source = _temp.PathOf("source");
        _replica = _temp.PathOf("replica");
        Directory.CreateDirectory(_source);

        _sink = new CollectingSink();
        _logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
        _synchronizer = new Synchronizer(
            _source,
            _replica,
            new DirectoryScanner(_logger),
            new SyncPlanner(new Md5FileComparer()),
            new SyncExecutor(_logger),
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _logger.Dispose();
        _temp.Dispose();
    }

    [Test]
    public void RunOnce_EmptyReplica_CopiesWholeTree()
    {
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("source/docs/b.txt", "b");
        Directory.CreateDirectory(_temp.PathOf("source/empty"));

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 2, Copied: 2, Updated: 0, Deleted: 0, Failed: 0)));
        Assert.That(_sink.Messages, Has.Some.StartsWith("Created replica folder"));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_NothingChanged_DoesNothing()
    {
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("source/docs/b.txt", "b");
        Sync();

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(0, 0, 0, 0, 0)));
    }

    [TestCase("longer content")]
    [TestCase("xyz")]
    public void RunOnce_ChangedFile_IsUpdated(string newContent)
    {
        _temp.WriteFile("source/a.txt", "abc");
        Sync();
        _temp.WriteFile("source/a.txt", newContent);

        var summary = Sync();

        Assert.That(summary?.Updated, Is.EqualTo(1));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_SameSizeAndTimestamp_IsStillUpdated()
    {
        var sourceFile = _temp.WriteFile("source/a.txt", "abc");
        Sync();
        var replicaTime = File.GetLastWriteTimeUtc(Path.Combine(_replica, "a.txt"));
        _temp.WriteFile("source/a.txt", "xyz");
        File.SetLastWriteTimeUtc(sourceFile, replicaTime);

        var summary = Sync();

        Assert.That(summary?.Updated, Is.EqualTo(1));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_TamperedReplica_NeverModifiesSource()
    {
        _temp.WriteFile("source/a.txt", "root a");
        _temp.WriteFile("source/docs/a.txt", "docs a");
        Sync();
        var sourceBefore = Describe(_source);

        File.WriteAllText(Path.Combine(_replica, "a.txt"), "changed in replica");
        _temp.WriteFile("replica/extra.txt", "extra");
        Directory.Delete(Path.Combine(_replica, "docs"), recursive: true);
        CreateDirectoryLink(Path.Combine(_replica, "docs"), _source);

        Sync();
        Sync();

        Assert.That(Describe(_source), Is.EqualTo(sourceBefore));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_RemovedFromSource_IsDeletedFromReplica()
    {
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("source/docs/b.txt", "b");
        Sync();
        File.Delete(_temp.PathOf("source/a.txt"));
        Directory.Delete(_temp.PathOf("source/docs"), recursive: true);

        var summary = Sync();

        Assert.That(summary?.Deleted, Is.EqualTo(3));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_ExtraContentInReplica_IsDeleted()
    {
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("replica/junk/x.txt", "x");
        _temp.WriteFile("replica/y.txt", "y");

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 1, Updated: 0, Deleted: 3, Failed: 0)));
        AssertReplicaMatchesSource();
    }

    [TestCase(FileAttributes.ReadOnly)]
    [TestCase(FileAttributes.Hidden)]
    [TestCase(FileAttributes.Hidden | FileAttributes.System)]
    public void RunOnce_ReplicaFilesWithAttributes_AreUpdatedAndDeleted(FileAttributes attributes)
    {
        _temp.WriteFile("source/update.txt", "old");
        _temp.WriteFile("source/delete.txt", "x");
        Sync();
        File.SetAttributes(Path.Combine(_replica, "update.txt"), attributes);
        File.SetAttributes(Path.Combine(_replica, "delete.txt"), attributes);
        _temp.WriteFile("source/update.txt", "new content");
        File.Delete(_temp.PathOf("source/delete.txt"));

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 1, Deleted: 1, Failed: 0)));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_ReadOnlyFoldersInReplica_AreDeleted()
    {
        Assume.That(OperatingSystem.IsWindows(), "The read-only folder attribute only blocks deletes on Windows.");
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("replica/old/inner/x.txt", "x");
        new DirectoryInfo(_temp.PathOf("replica/old")).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(_temp.PathOf("replica/old/inner")).Attributes |= FileAttributes.ReadOnly;

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 1, Updated: 0, Deleted: 3, Failed: 0)));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_CaseOnlyRename_IsMirrored()
    {
        _temp.WriteFile("source/readme.txt", "r");
        Sync();
        File.Move(_temp.PathOf("source/readme.txt"), _temp.PathOf("source/README.txt"));

        Sync();

        Assert.That(Directory.GetFiles(_replica).Select(Path.GetFileName), Is.EqualTo(new[] { "README.txt" }));
    }

    [Test]
    public void RunOnce_FileAndFolderSwapped_IsMirrored()
    {
        _temp.WriteFile("source/item", "file");
        Sync();

        File.Delete(_temp.PathOf("source/item"));
        _temp.WriteFile("source/item/inner.txt", "inner");
        Sync();
        AssertReplicaMatchesSource();

        Directory.Delete(_temp.PathOf("source/item"), recursive: true);
        _temp.WriteFile("source/item", "file again");
        Sync();
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_SourceMissing_SkipsAndKeepsReplica()
    {
        _temp.WriteFile("source/a.txt", "a");
        Sync();
        Directory.Delete(_source, recursive: true);

        var summary = Sync();

        Assert.That(summary, Is.Null);
        Assert.That(File.Exists(Path.Combine(_replica, "a.txt")), Is.True);
        Assert.That(_sink.Messages, Has.Some.Contains("skipped"));
    }

    [Test]
    public void RunOnce_LinkInSource_IsSkipped()
    {
        _temp.WriteFile("outside/secret.txt", "s");
        _temp.WriteFile("source/a.txt", "a");
        CreateDirectoryLink(_temp.PathOf("source/link"), _temp.PathOf("outside"));

        var summary = Sync();

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 1, Updated: 0, Deleted: 0, Failed: 0)));
        Assert.That(Directory.Exists(Path.Combine(_replica, "link")), Is.False);
        Assert.That(_sink.Messages, Has.Some.Contains("Not following symbolic link"));
    }

    [Test]
    public void RunOnce_LinkInReplica_IsReplacedAndItsTargetStaysUntouched()
    {
        _temp.WriteFile("outside/keep.txt", "keep");
        _temp.WriteFile("source/docs/a.txt", "a");
        Directory.CreateDirectory(_replica);
        CreateDirectoryLink(Path.Combine(_replica, "docs"), _temp.PathOf("outside"));

        var first = Sync();
        var second = Sync();

        Assert.That(first, Is.EqualTo(new SyncSummary(Created: 1, Copied: 1, Updated: 0, Deleted: 1, Failed: 0)));
        Assert.That(second, Is.EqualTo(new SyncSummary(0, 0, 0, 0, 0)));
        Assert.That(Directory.GetFiles(_temp.PathOf("outside")).Select(Path.GetFileName), Is.EqualTo(new[] { "keep.txt" }));
        Assert.That(new DirectoryInfo(Path.Combine(_replica, "docs")).LinkTarget, Is.Null);
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_LinkInReplicaCannotBeDeleted_NothingIsWrittenThroughIt()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "A read-only folder does not block deletes inside it on Windows.");
        _temp.WriteFile("outside/keep.txt", "keep");
        _temp.WriteFile("source/sub/docs/a.txt", "a");
        var sub = Path.Combine(_replica, "sub");
        Directory.CreateDirectory(sub);
        CreateDirectoryLink(Path.Combine(sub, "docs"), _temp.PathOf("outside"));

        SyncSummary? summary;
        using (DenyWriting(sub))
        {
            Assume.That(CanWrite(sub), Is.False, "Current user can still write the folder (e.g. running as root).");
            summary = Sync();
        }

        // the link delete fails, so creating "docs" and copying "docs/a.txt" are skipped instead of going through the link
        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 0, Deleted: 0, Failed: 3)));
        Assert.That(Directory.GetFiles(_temp.PathOf("outside")).Select(Path.GetFileName), Is.EqualTo(new[] { "keep.txt" }));
    }

    [Test]
    public void RunOnce_LockedReplicaFile_FailsAloneAndIsRetriedNextRun()
    {
        Assume.That(OperatingSystem.IsWindows(), "Exclusive file locks are only enforced on Windows.");
        _temp.WriteFile("source/locked.txt", "old");
        _temp.WriteFile("source/free.txt", "old");
        Sync();
        _temp.WriteFile("source/locked.txt", "new content");
        _temp.WriteFile("source/free.txt", "new content");

        SyncSummary? whileLocked;
        using (File.Open(Path.Combine(_replica, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            whileLocked = Sync();
        }

        var afterUnlock = Sync();

        Assert.That(whileLocked, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 1, Deleted: 0, Failed: 1)));
        Assert.That(afterUnlock, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 1, Deleted: 0, Failed: 0)));
        Assert.That(_sink.Messages, Has.Some.Contains("Failed to"));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_LockedSourceFile_KeepsOldCopyAndIsRetriedNextRun()
    {
        Assume.That(OperatingSystem.IsWindows(), "Exclusive file locks are only enforced on Windows.");
        _temp.WriteFile("source/locked.txt", "old");
        _temp.WriteFile("source/free.txt", "old");
        Sync();
        var lockedSource = _temp.WriteFile("source/locked.txt", "new content");
        _temp.WriteFile("source/free.txt", "new content");

        SyncSummary? whileLocked;
        using (File.Open(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            whileLocked = Sync();
        }

        var replicaWhileLocked = File.ReadAllText(Path.Combine(_replica, "locked.txt"));
        var afterUnlock = Sync();

        Assert.That(whileLocked, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 1, Deleted: 0, Failed: 1)));
        Assert.That(replicaWhileLocked, Is.EqualTo("old"));
        Assert.That(afterUnlock, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 1, Deleted: 0, Failed: 0)));
        AssertReplicaMatchesSource();
    }

    [Test]
    public void RunOnce_UnreadableSourceFolder_LeavesItsReplicaUntouched()
    {
        _temp.WriteFile("source/secret/a.txt", "a");
        _temp.WriteFile("source/open.txt", "o");
        Sync();
        File.Delete(_temp.PathOf("source/open.txt"));
        var secret = _temp.PathOf("source/secret");

        SyncSummary? summary;
        using (DenyListing(secret))
        {
            Assume.That(CanList(secret), Is.False, "Current user can still read the folder (e.g. running as root).");
            summary = Sync();
        }

        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 0, Deleted: 1, Failed: 0)));
        Assert.That(File.Exists(Path.Combine(_replica, "secret", "a.txt")), Is.True);
        Assert.That(_sink.Messages, Has.Some.Contains("Cannot read folder"));
    }

    [Test]
    public void RunOnce_UnreadableReplicaFolder_IsReportedAsFailed()
    {
        Directory.CreateDirectory(_temp.PathOf("source/locked"));
        _temp.WriteFile("replica/locked/stale.txt", "stale");
        var locked = _temp.PathOf("replica/locked");

        SyncSummary? summary;
        using (DenyListing(locked))
        {
            Assume.That(CanList(locked), Is.False, "Current user can still read the folder (e.g. running as root).");
            summary = Sync();
        }

        // its content is unknown, so the replica cannot be confirmed to match; must not look like a clean cycle
        Assert.That(summary, Is.EqualTo(new SyncSummary(Created: 0, Copied: 0, Updated: 0, Deleted: 0, Failed: 1)));
        Assert.That(_sink.Messages, Has.Some.Contains("Cannot read replica folder"));
    }

    [Test]
    public void RunOnce_ManyFiles_AreAllCopiedAndSecondRunIsNoOp()
    {
        const int FileCount = 2_000;
        for (var i = 0; i < FileCount; i++)
        {
            _temp.WriteFile($"source/batch{i % 10}/file{i}.txt", i.ToString());
        }

        var first = Sync();
        var second = Sync();

        Assert.That(first, Is.EqualTo(new SyncSummary(Created: 10, Copied: FileCount, Updated: 0, Deleted: 0, Failed: 0)));
        Assert.That(second, Is.EqualTo(new SyncSummary(0, 0, 0, 0, 0)));
    }

    [Test]
    public void RunOnce_LogsEachOperationAndSummary()
    {
        _temp.WriteFile("source/a.txt", "a");
        _temp.WriteFile("replica/old.txt", "o");

        Sync();

        Assert.That(_sink.Messages, Has.Some.Contains("Copied new file \"a.txt\""));
        Assert.That(_sink.Messages, Has.Some.Contains("Deleted file \"old.txt\""));
        Assert.That(_sink.Messages, Has.Some.StartsWith("Sync #1 done"));
    }

    [Test]
    public void RunOnce_Cancelled_StopsBeforeCopying()
    {
        _temp.WriteFile("source/a.txt", "a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _synchronizer.RunOnce(cancellation.Token));
        Assert.That(File.Exists(Path.Combine(_replica, "a.txt")), Is.False);
    }

    private SyncSummary? Sync() => _synchronizer.RunOnce(CancellationToken.None);

    private void AssertReplicaMatchesSource() => Assert.That(Describe(_replica), Is.EqualTo(Describe(_source)));

    // path plus content, so any difference shows up in the assert message
    private static List<string> Describe(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", AllEntries)
            .Select(path => Path.GetRelativePath(root, path) + (File.Exists(path) ? " = " + File.ReadAllText(path) : " (dir)"))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void CreateDirectoryLink(string link, string target) => Links.CreateDirectoryLink(link, target);

    private static IDisposable DenyListing(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDeniedListing(directory);
        }

        return new UnixDeniedListing(directory);
    }

    private static IDisposable DenyWriting(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A read-only folder does not block deletes inside it on Windows.");
        }

        return new UnixDeniedWriting(directory);
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, ".probe");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanList(string directory)
    {
        try
        {
            Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Keeps rendered log messages for assertions.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Messages { get; } = [];

        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }

    /// <summary>Denies folder listing for the current user until disposed.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsDeniedListing : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly FileSystemAccessRule _rule;

        public WindowsDeniedListing(string path)
        {
            _directory = new DirectoryInfo(path);
            _rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory,
                AccessControlType.Deny);

            var acl = _directory.GetAccessControl();
            acl.AddAccessRule(_rule);
            _directory.SetAccessControl(acl);
        }

        public void Dispose()
        {
            var acl = _directory.GetAccessControl();
            acl.RemoveAccessRule(_rule);
            _directory.SetAccessControl(acl);
        }
    }

    /// <summary>Removes all permissions from the folder until disposed.</summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class UnixDeniedListing : IDisposable
    {
        private readonly string _path;
        private readonly UnixFileMode _originalMode;

        public UnixDeniedListing(string path)
        {
            _path = path;
            _originalMode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        public void Dispose() => File.SetUnixFileMode(_path, _originalMode);
    }

    /// <summary>Leaves the folder readable but not writable until disposed, so nothing in it can be deleted.</summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class UnixDeniedWriting : IDisposable
    {
        private readonly string _path;
        private readonly UnixFileMode _originalMode;

        public UnixDeniedWriting(string path)
        {
            _path = path;
            _originalMode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose() => File.SetUnixFileMode(_path, _originalMode);
    }
}
