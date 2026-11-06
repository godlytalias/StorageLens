using System.Diagnostics;

namespace StorageLens.Services;

public static class FileExplorerService
{
    public static void Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("The item no longer exists.", path);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        if (Directory.Exists(path))
        {
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.ArgumentList.Add($"/select,{path}");
        }

        Process.Start(startInfo);
    }
}
