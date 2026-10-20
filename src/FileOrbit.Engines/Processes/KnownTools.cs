using FileOrbit.Core.Capabilities;

namespace FileOrbit.Engines.Processes;

/// <summary>Where optional tools are installed and how to describe them, shared by engines and the app.</summary>
public static class KnownTools
{
    public const string GhostscriptEngineId = "ghostscript";

    /// <summary>File name (without extension) of the optional Ghostscript engine plugin.</summary>
    public const string GhostscriptPlugin = "FileOrbit.Engines.Ghostscript";

    public static readonly string[] GhostscriptLocations =
    [
        @"%ProgramFiles%\gs\gs*\bin\gswin64c.exe",
        @"%ProgramFiles(x86)%\gs\gs*\bin\gswin32c.exe",
    ];

    public static readonly DependencyInfo Ghostscript = new(
        "Ghostscript",
        "PDF optimisation (smaller files) and PDF/A archival conversion",
        "GNU AGPL 3.0 (or commercial licence from Artifex)",
        "https://ghostscript.com/releases/gsdnld.html",
        // Ghostscript is not in the winget catalog, so the Engines page guides a manual download instead.
        InstallSteps:
        [
            "Click “Download Ghostscript”. Your browser opens the official Ghostscript download page.",
            "Under “Ghostscript AGPL Release”, download the installer for Windows (64 bit).",
            "Run the downloaded file and keep the default options.",
            "Come back here and click “Check again”. PDFs then get Optimize and PDF/A in the FileOrbit menu.",
        ]);

    public static string? FindGhostscript(ExecutableLocator locator) =>
        locator.Find(GhostscriptEngineId, "gswin64c.exe", GhostscriptLocations);
}
