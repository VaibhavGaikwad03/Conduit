using System.ServiceProcess;
using Conduit.Core.Logging;

namespace Conduit.Agent;

/// <summary>
/// Entry point for the ConduitAgent service. Runs as a Windows service by default; supports
/// `--install` / `--uninstall` (elevated) to register itself, and `--console` for local debugging.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--install": return ServiceInstaller.Install();
                case "--uninstall": return ServiceInstaller.Uninstall();
                case "--console": return RunConsole();
            }
        }

        ConduitLog.Initialize();
        try { ServiceBase.Run(new AgentService()); }
        catch (Exception ex) { ConduitLog.For("Agent").Error(ex, "Service host failed"); }
        finally { ConduitLog.Shutdown(); }
        return 0;
    }

    // Local debugging: run the pipe server in the foreground. Note that CreateProcessAsUser needs
    // SYSTEM privileges, so the helper launch only works when this runs as the service (or via
    // PsExec -s), not from a normal elevated console.
    private static int RunConsole()
    {
        ConduitLog.Initialize();
        using var server = new PipeServer();
        server.Start();
        Console.WriteLine("ConduitAgent running (console). Ctrl+C to exit.");
        using var wait = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; wait.Set(); };
        wait.Wait();
        ConduitLog.Shutdown();
        return 0;
    }
}
