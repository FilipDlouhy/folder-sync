namespace FolderSync.Sync;

public enum SyncAction
{
    DeleteFile,
    DeleteDirectory,
    DeleteLink,
    CreateDirectory,
    CopyFile,
    UpdateFile,
}

/// <summary>Single change to apply to the replica.</summary>
public sealed record SyncOperation(SyncAction Action, string RelativePath);

/// <summary>Successful operations per kind plus failures for one sync cycle.</summary>
public sealed record SyncSummary(int Created, int Copied, int Updated, int Deleted, int Failed);
