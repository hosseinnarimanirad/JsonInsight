using JsonInsight;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using WebJsonInsight.Components;
using WebJsonInsight.Platform;

namespace WebJsonInsight;

/// <summary>
/// The window, and the three lines of wiring that make the shared view models work in it.
///
/// <para>
/// The headless <c>--check</c> path is here too, and for the same reason the WPF app has one: it
/// reads Vault through the same loader the window does, so the two can never disagree about what a
/// tier holds. On Linux and macOS this is the only way to run it without a display.
/// </para>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                return CheckRunner.Run(args.Any(a => a.Equals("-v", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex}");
                return 2;
            }
        }

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.RootComponents.Add<App>("#app");

        // The Sources tab writes itself as it is edited. Switched on here rather than defaulted on,
        // so a test that pokes a row can never write the developer's real settings — see
        // VaultVm.WriteAsYouGo.
        VaultVm.WriteAsYouGo = true;

        // One window, so one of each. MainVm is the same object the WPF app builds at startup - it
        // owns the projects screen, the tabs, the change set and the Vault refresh.
        builder.Services.AddSingleton(_ => new MainVm());

        // Which dialog is open, and the guards in front of each one — the Blazor stand-in for the
        // ShowDialog calls in the WPF window's code-behind.
        builder.Services.AddSingleton<DialogService>();

        var app = builder.Build();

        app.MainWindow
            .SetTitle("WebJsonInsight")
            .SetUseOsDefaultSize(false)
            .SetSize(1560, 980)
            .SetUseOsDefaultLocation(false)
            .Center()
            .SetResizable(true);

        // Before the first render: MainVm reads the theme while it is being constructed, and the file
        // picker needs the window it will be modal to.
        PhotinoPlatform.Register(app.MainWindow);

        // A webview that dies takes the window's contents with it and leaves an empty frame, which
        // reads as a hang. Say what happened instead.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("WebJsonInsight", $"Unhandled error:\n\n{e.ExceptionObject}");

        app.Run();
        return 0;
    }
}
