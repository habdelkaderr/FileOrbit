namespace FileOrbit.Core.Shell;

public enum LaunchMode
{
    /// <summary>Show the radial menu for the given paths (context menu, "Open with", drag onto the exe).</summary>
    Open,

    /// <summary>Show the main window.</summary>
    MainWindow,

    /// <summary>Start silently in the notification area (used by "start with Windows").</summary>
    Background,

    RegisterShell,
    UnregisterShell,
}

/// <summary>Parsed command line. Paths are kept verbatim here; they are validated before use.</summary>
public sealed record CommandLine(LaunchMode Mode, IReadOnlyList<string> Paths)
{
    public const string OpenSwitch = "--open";
    public const string BackgroundSwitch = "--background";
    public const string MainSwitch = "--main";
    public const string RegisterSwitch = "--register-shell";
    public const string UnregisterSwitch = "--unregister-shell";

    /// <summary>
    /// Parses arguments as delivered by Windows (already split by the C runtime rules, so paths with
    /// spaces arrive as one argument when quoted by Explorer). Unknown switches are ignored.
    /// </summary>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var mode = (LaunchMode?)null;
        var paths = new List<string>();
        var onlyPaths = false;
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var arg = raw.Trim();
            if (!onlyPaths && arg == "--")
            {
                onlyPaths = true;
                continue;
            }

            if (!onlyPaths && arg.StartsWith("--", StringComparison.Ordinal))
            {
                mode ??= arg.ToLowerInvariant() switch
                {
                    OpenSwitch => LaunchMode.Open,
                    BackgroundSwitch => LaunchMode.Background,
                    MainSwitch => LaunchMode.MainWindow,
                    RegisterSwitch => LaunchMode.RegisterShell,
                    UnregisterSwitch => LaunchMode.UnregisterShell,
                    _ => null,
                };
                continue;
            }

            // Explorer can pass a trailing quote artefact for paths ending in a backslash ("C:\").
            paths.Add(arg.Trim('"'));
        }

        return new CommandLine(mode ?? (paths.Count > 0 ? LaunchMode.Open : LaunchMode.MainWindow), paths);
    }

    /// <summary>Serializes for forwarding to the running instance.</summary>
    public string[] ToArgs()
    {
        var modeSwitch = Mode switch
        {
            LaunchMode.Open => OpenSwitch,
            LaunchMode.Background => BackgroundSwitch,
            LaunchMode.RegisterShell => RegisterSwitch,
            LaunchMode.UnregisterShell => UnregisterSwitch,
            _ => MainSwitch,
        };
        return [modeSwitch, "--", .. Paths];
    }
}
