using System.Windows.Input;
using System.Windows.Interop;
using FileOrbit.App.Interop;

namespace FileOrbit.App.Shell;

/// <summary>A system-wide keyboard shortcut registered with RegisterHotKey on a message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4F52; // "OR"
    private readonly HwndSource _window;
    private bool _registered;

    public GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("FileOrbitHotkey")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public string? Current { get; private set; }

    /// <summary>Registers the shortcut, replacing any previous one. Returns false if it is invalid or taken.</summary>
    public bool TryRegister(string? gesture, out string? error)
    {
        Unregister();
        error = null;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return true;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            error = $"“{gesture}” is not a valid shortcut. Use a combination such as Ctrl+Alt+Space.";
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, modifiers | NativeMethods.ModNoRepeat, virtualKey))
        {
            error = $"{gesture} is already used by another application. Choose a different shortcut.";
            return false;
        }

        _registered = true;
        Current = gesture;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
            Current = null;
        }
    }

    /// <summary>Parses "Ctrl+Alt+Space"; at least one modifier is required so normal typing is unaffected.</summary>
    public static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "win" or "windows":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (key != Key.None || !Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        // Allow digits written as "1" rather than "D1".
                        if (part.Length == 1 && char.IsDigit(part[0]))
                        {
                            key = Key.D0 + (part[0] - '0');
                            break;
                        }

                        return false;
                    }

                    break;
            }
        }

        var modifierCount = System.Numerics.BitOperations.PopCount(modifiers);
        return key != Key.None && modifierCount > 0 && !(modifiers == NativeMethods.ModShift);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key >= Key.D0 && key <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
