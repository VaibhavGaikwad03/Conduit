namespace Conduit.Agent;

/// <summary>
/// A desktop the helper can be launched onto, paired with the token kind it needs. The interactive
/// <see cref="Interactive"/> desktop takes the user's elevated token; the <see cref="SecureDesktop"/>
/// Winlogon desktop (lock screen / UAC) takes the agent's SYSTEM token. See <see cref="HelperLauncher"/>.
/// </summary>
internal readonly record struct DesktopTarget(string Desktop, bool Secure)
{
    public static readonly DesktopTarget Interactive = new("WinSta0\\Default", false);
    public static readonly DesktopTarget SecureDesktop = new("WinSta0\\Winlogon", true);

    /// <summary>The right target for the current lock state of the console session.</summary>
    public static DesktopTarget ForLockState(bool locked) => locked ? SecureDesktop : Interactive;
}
