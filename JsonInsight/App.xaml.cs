using System.Runtime.InteropServices;
using System.Windows;
using JsonInsight.Themes;

namespace JsonInsight;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        if (args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(AttachParentProcess);
            try
            {
                Environment.ExitCode = CheckRunner.Run(args.Any(a => a.Equals("-v", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex}");
                Environment.ExitCode = 2;
            }

            Shutdown(Environment.ExitCode);
            return;
        }

        // Tell the shared view models what this host can do — clipboard, file picker, theme. Before
        // the first window, because MainVm reads the theme while it is being constructed.
        WpfPlatform.Register();

        // The Sources tab writes itself as it is edited. Switched on here rather than defaulted on,
        // so a test that pokes a row can never write the developer's real settings — see
        // VaultVm.WriteAsYouGo.
        ViewModels.VaultVm.WriteAsYouGo = true;

        // Opens in whichever theme Windows itself is set to; Ctrl+D switches it for the session.
        // Applied after the headless branches above, which shut down before there is a window.
        ThemeManager.Apply(ThemeManager.SystemTheme());

        base.OnStartup(e);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
