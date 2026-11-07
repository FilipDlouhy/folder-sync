using System.Diagnostics;

namespace FolderSync.Tests.TestSupport;

public static class Links
{
    // symlinks need admin or developer mode on Windows, junctions do not
    public static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var startInfo = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(link), Path.GetFullPath(target) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var mklink = Process.Start(startInfo)!;
        mklink.WaitForExit();
        Assert.That(mklink.ExitCode, Is.Zero, "mklink /J failed");
    }
}
