using Avalonia;
using Avalonia.Headless;
using Compositor.App;

// Attribute lives in Avalonia.Headless (base), discovered by HeadlessUnitTestSession.
[assembly: AvaloniaTestApplication(typeof(Compositor.App.Tests.TestApp))]

namespace Compositor.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            // Skia rather than the headless drawing stub, with the stub disabled, so a test can ask
            // for the rendered frame. The stub records no pixels, which would make the canvas
            // compositing tests unable to read back what was actually drawn.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
