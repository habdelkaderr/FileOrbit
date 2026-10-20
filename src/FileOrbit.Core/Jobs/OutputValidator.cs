using System.Text.Json;
using FileOrbit.Core.Capabilities;
using FileOrbit.Core.Detection;
using FileOrbit.Core.Formats;

namespace FileOrbit.Core.Jobs;

/// <summary>
/// Verifies that an engine really produced a file of the promised type, so that a renamed or
/// truncated file is never reported as a successful conversion.
/// </summary>
public sealed class OutputValidator
{
    private const long MaxJsonValidationBytes = 64 * 1024 * 1024;
    private readonly FileTypeDetector _detector;
    private readonly IReadOnlyList<IOutputContentCheck> _contentChecks;

    public OutputValidator(FileTypeDetector detector, IEnumerable<IOutputContentCheck>? contentChecks = null)
    {
        _detector = detector;
        _contentChecks = contentChecks?.ToList() ?? [];
    }

    public void Validate(string path, FileFormat expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ConversionException($"The engine did not produce the expected {expected.Name} file.");
        }

        if (info.Length == 0 && !expected.IsText)
        {
            throw new ConversionException($"The engine produced an empty {expected.Name} file.");
        }

        var detected = _detector.Detect(path);
        if (!Matches(expected, detected.Format))
        {
            throw new ConversionException(
                $"The output is not a valid {expected.Name} file.",
                $"Expected {expected.Id}, detected {detected.Format.Id} via {detected.Method}.");
        }

        foreach (var check in _contentChecks.Where(c => c.AppliesTo(expected)))
        {
            check.Check(path);
        }

        if (expected == KnownFormats.Json && info.Length is > 0 and <= MaxJsonValidationBytes)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var _ = JsonDocument.Parse(stream);
            }
            catch (JsonException ex)
            {
                throw new ConversionException("The engine produced invalid JSON.", ex.Message);
            }
        }
    }

    public static bool Matches(FileFormat expected, FileFormat actual)
    {
        if (expected == actual)
        {
            return true;
        }

        if (expected.IsText && actual.IsText)
        {
            return true;
        }

        return (expected.Id, actual.Id) switch
        {
            ("m4a", "mp4") or ("mp4", "m4a") or ("mov", "mp4") or ("mp4", "mov") => true,
            ("wma", "wmv") or ("wmv", "wma") => true,
            ("tgz", "gz") or ("gz", "tgz") => true,
            ("ogg", "opus") or ("opus", "ogg") => true,
            ("mkv", "webm") => true,
            _ => false,
        };
    }
}

/// <summary>
/// A deeper check of an output's content for one format (e.g. "a PDF must have at least one page").
/// Throws <see cref="ConversionException"/> when the output is unusable.
/// </summary>
public interface IOutputContentCheck
{
    bool AppliesTo(FileFormat format);

    void Check(string path);
}
