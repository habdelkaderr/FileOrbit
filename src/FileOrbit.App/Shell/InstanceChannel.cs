using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FileOrbit.App.Interop;
using FileOrbit.Core.Shell;

namespace FileOrbit.App.Shell;

/// <summary>
/// Keeps FileOrbit to one process per user session. Later launches (e.g. from the Explorer context
/// menu) forward their command line to the running instance over a named pipe that only the
/// current user can open, then exit immediately.
/// </summary>
public sealed class InstanceChannel : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _server;

    public InstanceChannel(string appName)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var session = Process.GetCurrentProcess().SessionId;
        _pipeName = $"{appName}.{sid}.{session}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{appName}.Instance", out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    /// <summary>Sends the command line to the primary instance. Returns false if it could not be reached.</summary>
    public bool TryForward(CommandLine commandLine, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);

            // The primary replies with its process id so we can hand over the right to take the foreground:
            // this process was started by the user's click in Explorer and may activate windows; the
            // background instance otherwise may not.
            var pidBytes = new byte[4];
            client.ReadExactly(pidBytes);
            NativeMethods.AllowSetForegroundWindow(BitConverter.ToInt32(pidBytes));

            var payload = JsonSerializer.SerializeToUtf8Bytes(commandLine.ToArgs());
            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();
            client.ReadByte(); // Wait for acknowledgement so we don't exit before delivery.
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts listening for forwarded command lines. The callback runs on a background thread.</summary>
    public void StartServer(Action<CommandLine> onCommand)
    {
        _server = new CancellationTokenSource();
        var token = _server.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), token);

                    var lengthBytes = new byte[4];
                    await server.ReadExactlyAsync(lengthBytes, token);
                    var length = BitConverter.ToInt32(lengthBytes);
                    if (length is <= 0 or > MaxMessageBytes)
                    {
                        continue;
                    }

                    var payload = new byte[length];
                    await server.ReadExactlyAsync(payload, token);
                    server.WriteByte(1);
                    var args = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(payload)) ?? [];
                    onCommand(CommandLine.Parse(args));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException or EndOfStreamException)
                {
                    // A malformed or interrupted message; keep serving.
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _server?.Cancel();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }
}
