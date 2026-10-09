using Avalonia;
using Compositor.Core;
using Compositor.Core.Project;
using Avalonia.Controls.ApplicationLifetimes;

namespace Compositor.App;

public static class Program
{
    public static int Main(string[] args)
    {
        // `--smoke`: headless entry (CI) that renders, composites, and round-trips a project without
        // opening a window. This is what a packaged build is checked against, so it exercises the
        // native Skia codecs as well as the managed ones.
        if (args.Contains("--smoke"))
        {
            return Smoke.Run(Console.Out);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
