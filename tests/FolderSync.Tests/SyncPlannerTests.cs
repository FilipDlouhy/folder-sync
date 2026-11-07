using FolderSync.Sync;

namespace FolderSync.Tests;

/// <summary>Planner tests on in-memory snapshots, no disk access.</summary>
public sealed class SyncPlannerTests
{
    private const string SourceRoot = "source";
    private const string ReplicaRoot = "replica";

    [Test]
    public void Plan_EmptyReplica_CreatesDirectoriesBeforeCopyingFiles()
    {
        var source = Snapshot(Dir("docs"), File("docs/a.txt", 1), File("b.txt", 1));

        var plan = Plan(source, Snapshot(), new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.CreateDirectory, "docs"),
            Op(SyncAction.CopyFile, "b.txt"),
            Op(SyncAction.CopyFile, "docs/a.txt"),
        }));
    }

    [Test]
    public void Plan_IdenticalTrees_ReturnsNoOperations()
    {
        var source = Snapshot(Dir("docs"), File("docs/a.txt", 3));
        var replica = Snapshot(Dir("docs"), File("docs/a.txt", 3));

        var plan = Plan(source, replica, new FakeComparer(equal: true));

        Assert.That(plan, Is.Empty);
    }

    [Test]
    public void Plan_DifferentSize_UpdatesWithoutComparingContent()
    {
        var comparer = new FakeComparer(equal: true);

        var plan = Plan(Snapshot(File("a.txt", 1)), Snapshot(File("a.txt", 2)), comparer);

        Assert.That(plan, Is.EqualTo(new[] { Op(SyncAction.UpdateFile, "a.txt") }));
        Assert.That(comparer.Calls, Is.Empty);
    }

    [Test]
    public void Plan_SameSizeDifferentContent_UpdatesFile()
    {
        var comparer = new FakeComparer(equal: false);

        var plan = Plan(Snapshot(File("a.txt", 3)), Snapshot(File("a.txt", 3)), comparer);

        Assert.That(plan, Is.EqualTo(new[] { Op(SyncAction.UpdateFile, "a.txt") }));
        Assert.That(comparer.Calls, Is.EqualTo(new[]
        {
            (Path.Combine(SourceRoot, "a.txt"), Path.Combine(ReplicaRoot, "a.txt")),
        }));
    }

    [Test]
    public void Plan_ExtraReplicaEntries_DeletesFilesFirstThenDeepestDirectories()
    {
        var replica = Snapshot(Dir("old"), Dir("old/deep"), File("old/deep/x.txt", 1), File("y.txt", 1));

        var plan = Plan(Snapshot(), replica, new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteFile, "old/deep/x.txt"),
            Op(SyncAction.DeleteFile, "y.txt"),
            Op(SyncAction.DeleteDirectory, "old/deep"),
            Op(SyncAction.DeleteDirectory, "old"),
        }));
    }

    [Test]
    public void Plan_FileReplacedByDirectory_DeletesFileBeforeCreatingDirectory()
    {
        var plan = Plan(Snapshot(Dir("item")), Snapshot(File("item", 1)), new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteFile, "item"),
            Op(SyncAction.CreateDirectory, "item"),
        }));
    }

    [Test]
    public void Plan_DirectoryReplacedByFile_DeletesDirectoryBeforeCopyingFile()
    {
        var replica = Snapshot(Dir("item"), File("item/inner.txt", 1));

        var plan = Plan(Snapshot(File("item", 1)), replica, new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteFile, "item/inner.txt"),
            Op(SyncAction.DeleteDirectory, "item"),
            Op(SyncAction.CopyFile, "item"),
        }));
    }

    [Test]
    public void Plan_CaseOnlyRename_ReplacesFileWithNewName()
    {
        var plan = Plan(Snapshot(File("README.txt", 1)), Snapshot(File("readme.txt", 1)), new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteFile, "readme.txt"),
            Op(SyncAction.CopyFile, "README.txt"),
        }));
    }

    [Test]
    public void Plan_UnreadableSourceDirectory_KeepsItsReplicaContent()
    {
        var source = Snapshot([Dir("secret")], unreadable: ["secret"]);
        var replica = Snapshot(Dir("secret"), File("secret/a.txt", 1), Dir("secret2"), File("secret2/b.txt", 1));

        var plan = Plan(source, replica, new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteFile, "secret2/b.txt"),
            Op(SyncAction.DeleteDirectory, "secret2"),
        }));
    }

    [Test]
    public void Plan_LinkInReplica_IsDeletedAndReplacedByRealFolder()
    {
        var source = Snapshot(Dir("docs"), File("docs/a.txt", 1));

        var plan = Plan(source, Snapshot(Link("docs")), new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[]
        {
            Op(SyncAction.DeleteLink, "docs"),
            Op(SyncAction.CreateDirectory, "docs"),
            Op(SyncAction.CopyFile, "docs/a.txt"),
        }));
    }

    [Test]
    public void Plan_LinkInSource_IsIgnored()
    {
        var plan = Plan(Snapshot(Link("shortcut"), File("a.txt", 1)), Snapshot(File("a.txt", 1)), new FakeComparer(equal: true));

        Assert.That(plan, Is.Empty);
    }

    [Test]
    public void Plan_LinkOnBothSides_IsStillRemovedFromReplica()
    {
        var plan = Plan(Snapshot(Link("shortcut")), Snapshot(Link("shortcut")), new FakeComparer(equal: true));

        Assert.That(plan, Is.EqualTo(new[] { Op(SyncAction.DeleteLink, "shortcut") }));
    }

    [Test]
    public void Plan_Cancelled_StopsBeforeComparingFiles()
    {
        var comparer = new FakeComparer(equal: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new SyncPlanner(comparer)
            .Plan(SourceRoot, Snapshot(File("a.txt", 1)), ReplicaRoot, Snapshot(File("a.txt", 1)), cancellation.Token));
        Assert.That(comparer.Calls, Is.Empty);
    }

    private static IReadOnlyList<SyncOperation> Plan(Snapshot source, Snapshot replica, IFileComparer comparer) =>
        new SyncPlanner(comparer).Plan(SourceRoot, source, ReplicaRoot, replica);

    private static Snapshot Snapshot(params FileSystemEntry[] entries) => Snapshot(entries, unreadable: []);

    private static Snapshot Snapshot(FileSystemEntry[] entries, string[] unreadable) =>
        new(entries.ToDictionary(entry => entry.RelativePath), unreadable.Select(Normalize).ToHashSet());

    private static FileSystemEntry File(string path, long size) => FileSystemEntry.ForFile(Normalize(path), size);

    private static FileSystemEntry Dir(string path) => FileSystemEntry.ForDirectory(Normalize(path));

    private static FileSystemEntry Link(string path) => FileSystemEntry.ForLink(Normalize(path));

    private static SyncOperation Op(SyncAction action, string path) => new(action, Normalize(path));

    // tests are written with '/', snapshots use the OS separator
    private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Returns a fixed answer and records which files were compared.</summary>
    private sealed class FakeComparer(bool equal) : IFileComparer
    {
        public List<(string Source, string Replica)> Calls { get; } = [];

        public bool AreEqual(string sourceFile, string replicaFile)
        {
            Calls.Add((sourceFile, replicaFile));
            return equal;
        }
    }
}
