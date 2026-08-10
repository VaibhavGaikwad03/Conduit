using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Conduit.Agent;

/// <summary>
/// Shared pipe security for the agent's local IPC. Both the app-facing pipe and the per-launch
/// helper pipe are created by the LocalSystem service, so they must explicitly grant the interactive
/// user access — otherwise the user-session app (and a helper launched under the user token on the
/// normal desktop) can't open them. Access is limited to the interactive user + SYSTEM; the pipes are
/// never network-reachable.
/// </summary>
internal static class AgentPipe
{
    private static PipeSecurity Security()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return s;
    }

    public static NamedPipeServerStream Create(string name, int maxInstances) =>
        NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, maxInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
}
