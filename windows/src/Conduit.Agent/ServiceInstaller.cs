using System.Diagnostics;
using Conduit.Core.Agent;

namespace Conduit.Agent;

/// <summary>
/// Registers / removes the agent as a LocalSystem auto-start service via sc.exe. Must be run
/// elevated (the app triggers this with the same one-time UAC pattern WebcamService uses).
/// </summary>
internal static class ServiceInstaller
{
    public static int Install()
    {
        var exe = Environment.ProcessPath!;
        // Quoting note: sc parses binPath oddly — the value must be one token with an inner quote.
        Run($"create {AgentIpc.ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem DisplayName= \"Conduit Agent\"");
        Run($"description {AgentIpc.ServiceName} \"Brokers Conduit desktop capture/control across desktops (incl. the lock screen).\"");
        Run($"start {AgentIpc.ServiceName}");
        return 0;
    }

    public static int Uninstall()
    {
        Run($"stop {AgentIpc.ServiceName}");
        Run($"delete {AgentIpc.ServiceName}");
        return 0;
    }

    private static void Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p!.WaitForExit();
            Console.WriteLine($"sc {args.Split(' ')[0]} -> {p.ExitCode}: {p.StandardOutput.ReadToEnd().Trim()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sc {args}: {ex.Message}");
        }
    }
}
