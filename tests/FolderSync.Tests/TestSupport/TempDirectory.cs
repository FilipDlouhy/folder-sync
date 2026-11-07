namespace FolderSync.Tests.TestSupport;

/// <summary>Unique temp folder per test, deleted with everything inside on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,          // default would skip hidden files
    };

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "FolderSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

    public string WriteFile(string relativePath, string content)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    public void Dispose()
    {
        try
        {
            // ---- remove links first so cleanup never touches their targets
            foreach (var link in Directory.EnumerateDirectories(Root, "*", AllFiles).Where(IsLink).ToList())
            {
                Directory.Delete(link);
            }

            // ---- clear attributes, read-only files and folders would block the delete
            foreach (var file in Directory.EnumerateFiles(Root, "*", AllFiles))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            foreach (var directory in Directory.EnumerateDirectories(Root, "*", AllFiles))
            {
                File.SetAttributes(directory, FileAttributes.Directory);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leftover temp folder must not fail the test
            TestContext.Progress.WriteLine($"Could not clean up {Root}: {ex.Message}");
        }
    }
}
