using FileOrbit.Core.Capabilities;

namespace FileOrbit.Engines.Media;

/// <summary>Video options shared by the media engines, so equivalent conversions offer the same choices.</summary>
internal static class MediaOptions
{
    internal static readonly OptionDescriptor ResolutionOption = OptionDescriptor.Choice("height", "Resolution", "0",
        new("0", "Same as source"),
        new("2160", "4K (2160p)"),
        new("1080", "1080p"),
        new("720", "720p"),
        new("480", "480p")) with
    {
        Description = "Video is only ever scaled down",
    };

    internal static readonly OptionDescriptor VideoQualityOption = OptionDescriptor.Choice("quality", "Quality", "balanced",
        new("high", "High", "Larger files"),
        new("balanced", "Balanced"),
        new("small", "Small file", "Visible compression on fast motion"));
}
