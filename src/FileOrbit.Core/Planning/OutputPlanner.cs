using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Planning;

/// <summary>What to do when the planned output already exists.</summary>
public enum CollisionPolicy
{
    /// <summary>Pick a free name such as "Report (1).pdf". The safe default.</summary>
    KeepBoth,

    /// <summary>Replace the existing file. Only ever used after an explicit user choice; never the source.</summary>
    Replace,

    /// <summary>Do not convert this file.</summary>
    Skip,
}

/// <summary>A planned destination for one conversion.</summary>
public sealed record OutputPlan(
    string Directory,
    string BaseName,
    string Extension,
    OutputShape Shape)
{
    public string FilePath => Path.Combine(Directory, BaseName + Extension);

    public string FolderPath => Path.Combine(Directory, BaseName);

    /// <summary>The path the user will most likely see as the result.</summary>
    public string PrimaryPath => Shape == OutputShape.Folder ? FolderPath : FilePath;

    public bool PrimaryExists => Shape == OutputShape.Folder ? System.IO.Directory.Exists(FolderPath) || File.Exists(FolderPath) : File.Exists(FilePath) || System.IO.Directory.Exists(FilePath);
}

public static class OutputPlanner
{
    public const int MaxCollisionAttempts = 10_000;

    /// <summary>Default base name for an action, e.g. "Report" or "Report (resized)".</summary>
    public static string DefaultBaseName(DetectedFile input, ConversionAction action)
    {
        var baseName = PathSafety.SanitizeFileName(input.BaseName, "converted");
        var suffix = action.Best.Capability.OutputSuffix ?? action.Variant ?? "edited";
        return action.Kind == ActionKind.Transform ? $"{baseName} ({suffix})" : baseName;
    }

    public static OutputPlan Plan(DetectedFile input, ConversionAction action, string outputDirectory, string? baseNameOverride = null)
    {
        var directory = PathSafety.ValidateOutputDirectory(outputDirectory);
        var baseName = PathSafety.SanitizeFileName(string.IsNullOrWhiteSpace(baseNameOverride) ? DefaultBaseName(input, action) : baseNameOverride);
        var shape = action.Kind == ActionKind.Extract ? OutputShape.Folder : action.Best.Capability.Shape;
        return new OutputPlan(directory, baseName, action.Target.PrimaryExtension, shape);
    }

    /// <summary>
    /// Returns <paramref name="desired"/> if nothing exists there, otherwise the first free
    /// "name (n).ext" variant. Works for files and folders and for compound extensions.
    /// </summary>
    public static string GetAvailablePath(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, baseName + extension);
        for (var i = 1; Exists(candidate); i++)
        {
            if (i > MaxCollisionAttempts)
            {
                throw new IOException("Could not find a free output file name.");
            }

            candidate = Path.Combine(directory, $"{baseName} ({i}){extension}");
        }

        return candidate;
    }

    /// <summary>
    /// Decides the final path for an output under a collision policy. Returns null when the policy
    /// says to skip. Never returns a path equal to <paramref name="sourcePath"/>.
    /// </summary>
    public static string? ResolveCollision(string directory, string baseName, string extension, CollisionPolicy policy, string sourcePath)
    {
        var desired = Path.Combine(directory, baseName + extension);
        var isSource = PathSafety.IsSamePath(desired, sourcePath);
        if (!Exists(desired))
        {
            return desired;
        }

        return policy switch
        {
            CollisionPolicy.Replace when !isSource && File.Exists(desired) => desired,
            CollisionPolicy.Skip when !isSource => null,
            _ => GetAvailablePath(directory, baseName, extension),
        };
    }

    private static bool Exists(string path) => File.Exists(path) || System.IO.Directory.Exists(path);
}
