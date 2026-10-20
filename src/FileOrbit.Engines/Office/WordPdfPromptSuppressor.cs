using Microsoft.Win32;

namespace FileOrbit.Engines.Office;

/// <summary>
/// Before PDF Reflow, Word shows "Word will now convert your PDF to an editable Word document". In
/// an invisible automation instance nobody can dismiss it, so the conversion would block. This turns
/// the notice off for the duration of one conversion and then restores the user's previous setting.
/// </summary>
internal sealed class WordPdfPromptSuppressor : IDisposable
{
    private const string ValueName = "DisableConvertPdfWarning";
    private readonly string _keyPath;
    private readonly object? _previous;

    private WordPdfPromptSuppressor(string keyPath)
    {
        _keyPath = keyPath;
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        _previous = key.GetValue(ValueName);
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);
    }

    /// <param name="wordVersion">Word's version, e.g. "16.0" (its options live under Office\16.0\Word\Options).</param>
    public static WordPdfPromptSuppressor? Suppress(string wordVersion)
    {
        var parts = wordVersion.Split('.');
        var version = parts.Length >= 2 && int.TryParse(parts[0], out _) ? $"{parts[0]}.{parts[1]}" : "16.0";
        try
        {
            return new WordPdfPromptSuppressor($@"Software\Microsoft\Office\{version}\Word\Options");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (_previous is null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(ValueName, _previous);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }
}
