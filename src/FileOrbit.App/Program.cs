using FileOrbit.App.Shell;
using FileOrbit.Core.Shell;

namespace FileOrbit.App;

public static class Program
{
    public const string AppUserModelId = "FileOrbit.App";

    [STAThread]
    public static int Main(string[] args)
    {
        var commandLine = CommandLine.Parse(args);

        // Installer hooks: register or remove Explorer integration and exit without UI.
        if (commandLine.Mode is LaunchMode.RegisterShell or LaunchMode.UnregisterShell)
        {
            var registration = new ShellRegistration(ShellRegistration.CurrentExecutable);
            if (commandLine.Mode == LaunchMode.RegisterShell)
            {
                registration.Register();
            }
            else
            {
                registration.Unregister();
                ShellRegistration.SetStartWithWindows(false);
            }

            return 0;
        }

        using var instance = new InstanceChannel("FileOrbit");
        if (!instance.IsPrimary && instance.TryForward(commandLine, TimeSpan.FromSeconds(3)))
        {
            return 0; // Fast path: the running instance shows the menu.
        }

        Interop.NativeMethods.SetCurrentProcessExplicitAppUserModelId(AppUserModelId);
        var app = new App(instance, commandLine);
        app.InitializeComponent();
        return app.Run();
    }
}
