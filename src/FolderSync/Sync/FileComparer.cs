using System.Security.Cryptography;

namespace FolderSync.Sync;

/// <summary>Decides whether two files have the same content.</summary>
public interface IFileComparer
{
    bool AreEqual(string sourceFile, string replicaFile);
}

/// <summary>Compares sizes first, then MD5 hashes of the content.</summary>
public sealed class Md5FileComparer : IFileComparer
{
    public bool AreEqual(string sourceFile, string replicaFile)
    {
        try
        {
            if (new FileInfo(sourceFile).Length != new FileInfo(replicaFile).Length)
            {
                return false;
            }

            return ComputeHash(sourceFile).SequenceEqual(ComputeHash(replicaFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or vanished file counts as changed, the copy then retries or reports it
            return false;
        }
    }

    private static byte[] ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return MD5.HashData(stream);
    }
}
