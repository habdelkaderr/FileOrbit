using System.Runtime.InteropServices;
using System.Windows.Input;
using FileOrbit.App.Shell;
using FileOrbit.Core.Shell;
using FileOrbit.Engines.Data;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed partial class ShellIntegrationTests
{
    [Fact]
    public void Explorer_verb_command_round_trips_awkward_paths()
    {
        // Explorer substitutes %1 into the registered command and Windows splits it with the
        // CommandLineToArgvW rules; the path must arrive intact as one argument.
        var registration = new ShellRegistration(@"C:\Program Files\FileOrbit\FileOrbit.exe");
        foreach (var path in new[]
                 {
                     @"C:\Users\Ada Lovelace\Documents\Q3 report (final) & notes.pdf",
                     @"D:\Données\été 2024\ﾌｧｲﾙ #1 [draft]; v2,3.docx",
                     @"\\server\share\with  two spaces\file.txt",
                     @"C:\percent%20and%PATH%\file.png",
                 })
        {
            var command = registration.Command.Replace("%1", path);
            var argv = SplitCommandLine(command);

            Assert.Equal(@"C:\Program Files\FileOrbit\FileOrbit.exe", argv[0]);
            var parsed = CommandLine.Parse(argv[1..]);
            Assert.Equal(LaunchMode.Open, parsed.Mode);
            Assert.Equal([path], parsed.Paths);
        }
    }

    [Fact]
    public void Command_line_modes_and_paths()
    {
        Assert.Equal(LaunchMode.MainWindow, CommandLine.Parse([]).Mode);
        Assert.Equal(LaunchMode.Background, CommandLine.Parse(["--background"]).Mode);
        Assert.Equal(LaunchMode.Open, CommandLine.Parse([@"C:\a.pdf"]).Mode);
        Assert.Equal(LaunchMode.RegisterShell, CommandLine.Parse(["--register-shell"]).Mode);

        var withDashes = CommandLine.Parse(["--open", "--", "--weird-name.txt", @"C:\b.pdf"]);
        Assert.Equal(["--weird-name.txt", @"C:\b.pdf"], withDashes.Paths);

        var unknown = CommandLine.Parse(["--something-new", @"C:\c.pdf"]);
        Assert.Equal(LaunchMode.Open, unknown.Mode);
        Assert.Equal([@"C:\c.pdf"], unknown.Paths);
    }

    [Fact]
    public void Forwarded_command_line_round_trips()
    {
        var original = new CommandLine(LaunchMode.Open, [@"C:\x y\--z.pdf", @"D:\ü.png"]);
        var parsed = CommandLine.Parse(original.ToArgs());
        Assert.Equal(original.Mode, parsed.Mode);
        Assert.Equal(original.Paths, parsed.Paths);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", true)]
    [InlineData("ctrl + shift + c", true)]
    [InlineData("Win+Alt+1", true)]
    [InlineData("Shift+A", false)]
    [InlineData("A", false)]
    [InlineData("Ctrl+NotAKey", false)]
    [InlineData("Ctrl+A+B", false)]
    public void Hotkey_parsing(string gesture, bool valid) =>
        Assert.Equal(valid, GlobalHotkey.TryParse(gesture, out _, out _));

    [Fact]
    public void Hotkey_format_round_trips()
    {
        var text = GlobalHotkey.Format(ModifierKeys.Control | ModifierKeys.Alt, Key.Space);
        Assert.Equal("Ctrl+Alt+Space", text);
        Assert.True(GlobalHotkey.TryParse(text, out _, out var key));
        Assert.Equal(Key.Space, key);
    }

    [Theory]
    [InlineData("=SUM(A1:A3)", true)]
    [InlineData("@cmd", true)]
    [InlineData("+cmd|' /C calc'!A0", true)]
    [InlineData("-2+3", false)]
    [InlineData("+44 20 7946 0000", false)]
    [InlineData("-12.5", false)]
    [InlineData("hello", false)]
    public void Csv_formula_detection(string value, bool isFormula) =>
        Assert.Equal(isFormula, CsvFormat.LooksLikeFormula(value));

    [Fact]
    public void Csv_reader_handles_quotes_newlines_and_delimiters()
    {
        var rows = CsvFormat.Read(new StringReader("a;b\r\n\"x;y\";\"line1\nline2\"\r\n\"say \"\"hi\"\"\";\r\n"), ';');
        Assert.Equal(3, rows.Count);
        Assert.Equal(["x;y", "line1\nline2"], rows[1]);
        Assert.Equal(["say \"hi\"", ""], rows[2]);
        Assert.Equal(';', CsvFormat.DetectDelimiter("a;b;c\n1;2;3\n"));
        Assert.Equal('\t', CsvFormat.DetectDelimiter("a\tb\n1\t2\n"));
        Assert.Equal(',', CsvFormat.DetectDelimiter("name,note\nx,\"a;b;c;d\"\n"));
    }

    [Fact]
    public void Instance_channel_forwards_to_primary()
    {
        var name = "FileOrbitTest" + Guid.NewGuid().ToString("N")[..8];
        using var primary = new InstanceChannel(name);
        Assert.True(primary.IsPrimary);
        var received = new TaskCompletionSource<CommandLine>();
        primary.StartServer(command => received.TrySetResult(command));

        using var secondary = new InstanceChannel(name);
        Assert.False(secondary.IsPrimary);
        var sent = new CommandLine(LaunchMode.Open, [@"C:\Users\Ada\Q3 report (final).pdf"]);
        Assert.True(secondary.TryForward(sent, TimeSpan.FromSeconds(5)));

        Assert.True(received.Task.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(sent.Paths, received.Task.Result.Paths);
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
