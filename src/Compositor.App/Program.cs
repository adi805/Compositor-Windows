using System.Diagnostics;
using Avalonia;
using Compositor.App.Update;
using Compositor.Core;
using Compositor.Core.Project;
using Avalonia.Controls.ApplicationLifetimes;

namespace Compositor.App;

public static class Program
{
    /// <summary>Argument the staged launcher passes to run the swap instead of opening the editor.</summary>
    public const string ApplyUpdateSwitch = "--apply-update";

    public static int Main(string[] args)
    {
        // `--smoke`: headless entry (CI) that exercises the document model and
        // project I/O without opening a window. Default: launch the UI.
        if (args.Contains("--smoke"))
        {
            var doc = new Document(64, 64);
            doc.AddLayer(new Layer("Background"));
            var path = Path.Combine(Path.GetTempPath(), $"compositor-smoke-{Guid.NewGuid():N}.comp");
            ProjectStore.Save(doc, path);
            var reloaded = ProjectStore.Load(path);
            File.Delete(path);
            Console.WriteLine(
                $"Compositor.Windows pre-alpha: doc {reloaded.Width}x{reloaded.Height}, " +
                $"layers={reloaded.Layers.Count}, round-trip OK");
            return 0;
        }

        // `--apply-update <staging>`: what the staged launcher runs. The swap is done here, in the
        // application, rather than in a generated shell script: a script that interpolates filesystem
        // paths into nested batch and PowerShell commands has to get quoting right for every path the
        // user could have, and .NET passes an argument without any quoting rules at all.
        if (ApplyUpdate(args) is { } exitCode)
        {
            return exitCode;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Runs the staged update when asked, reporting the outcome on the console. Null when this launch is
    /// not an update run, which is how the normal path stays untouched.
    /// </summary>
    private static int? ApplyUpdate(string[] args)
    {
        var index = Array.IndexOf(args, ApplyUpdateSwitch);
        if (index < 0)
        {
            return null;
        }

        var staging = index + 1 < args.Length
            ? Path.GetFullPath(args[index + 1])
            : Path.Combine(AppContext.BaseDirectory, UpdateStager.StagingDirectoryName);

        var install = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var result = UpdateApplier.Apply(
            staging,
            install,
            waitForExit: () => WaitForApplicationExit(),
            relaunch: Start);
        Console.WriteLine(result.Succeeded ? result.Reason : $"Update failed: {result.Reason}");
        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Waits for other copies of this executable to exit. Replacing files a running process holds open
    /// fails partway through, which is the one way the swap can leave a half-updated install.
    /// </summary>
    private static bool WaitForApplicationExit()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        var deadline = DateTime.UtcNow + UpdateApplier.ExitWait;
        while (DateTime.UtcNow < deadline)
        {
            var others = Process.GetProcessesByName(name)
                .Where(p => p.Id != Environment.ProcessId)
                .ToList();
            foreach (var process in others)
            {
                process.Dispose();
            }

            if (others.Count == 0)
            {
                return true;
            }

            Thread.Sleep(UpdateApplier.ExitPollInterval);
        }

        return false;
    }

    private static void Start(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The update is applied; not relaunching is a nuisance the user can fix by clicking the icon.
            Console.WriteLine($"The update was applied but the app could not be started: {ex.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
