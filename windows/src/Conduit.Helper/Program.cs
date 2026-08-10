using System.IO.Pipes;
using System.Runtime.InteropServices;
using Conduit.Core.Agent;
using Conduit.Core.Logging;

namespace Conduit.Helper;

/// <summary>
/// Desktop-bound capture + input worker. The LocalSystem agent launches one of these onto a specific
/// desktop (Default while unlocked; Winsta0\Winlogon later, for the lock screen) and hands it a pipe
/// name. The helper streams that desktop's H.264 up to the agent and injects the input the agent
/// relays down — all as opaque bytes / fixed structs, never JSON. See the locked-PC scope.
/// </summary>
internal static class Program
{
    private static NamedPipeClientStream? _pipe;

    [STAThread]
    private static int Main(string[] args)
    {
        var pipeName = GetArg(args, "--pipe");
        if (pipeName is null) return 2;

        ConduitLog.Initialize();
        var log = ConduitLog.For("Helper");

        try
        {
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(10_000);
            AgentFrame.Write(_pipe, AgentMsg.Hello, ReadOnlySpan<byte>.Empty);
            log.Information("Connected to agent on {Pipe}", pipeName);

            if (!NativeDesktop.Start(OnFrame))
            {
                log.Error("Native desktop capture failed to start");
                return 3;
            }

            // Block reading input until the agent closes the pipe or sends StopCapture.
            while (AgentFrame.Read(_pipe, out var type, out var payload))
            {
                if (type == AgentMsg.Input) Win32Input.Apply(InputMsg.FromBytes(payload));
                else if (type == AgentMsg.StopCapture) break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Helper ending");
        }
        finally
        {
            NativeDesktop.Stop();
            try { _pipe?.Dispose(); } catch { /* closing */ }
            ConduitLog.Shutdown();
        }
        return 0;
    }

    // Called on the native capture thread, once per encoded access unit.
    private static void OnFrame(IntPtr data, int len)
    {
        var p = _pipe;
        if (p is null || len <= 0) return;
        var buf = new byte[len];
        Marshal.Copy(data, buf, 0, len);
        try { AgentFrame.Write(p, AgentMsg.Frame, buf); }
        catch { /* pipe closing; capture stops in finally */ }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
