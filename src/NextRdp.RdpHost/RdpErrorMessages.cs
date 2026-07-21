namespace NextRdp.RdpHost;

internal static class RdpErrorMessages
{
    public static bool IsExpectedDisconnect(int reason) => reason is 0 or 1 or 2 or 11 or 12;

    public static string Disconnect(int reason) => reason switch
    {
        0 => "The RDP session disconnected cleanly.",
        1 => "The RDP session was disconnected by the client application.",
        2 => "The remote session logged off.",
        3 => "The server ended the session after its idle timeout. Reconnect if access is still required.",
        4 => "The server logon timeout expired before sign-in completed. Check connectivity and try again.",
        5 => "Another connection replaced this RDP session.",
        6 => "The server ended the session because it ran out of memory.",
        7 => "The server denied the connection. Verify Remote Desktop access and account permissions.",
        8 => "The server denied the connection because its required security mode is unavailable.",
        9 => "The account does not have sufficient privileges for this remote session.",
        10 => "The server requires fresh credentials. Re-enter credentials in the Windows prompt.",
        11 => "The remote system ended the connection.",
        12 => "The session was disconnected by the signed-in user.",
        _ => $"The RDP session ended unexpectedly (reason {reason}). Check the target event log, network path, account state, and RDP policy."
    };

    public static string DisconnectCategory(int reason) => reason switch
    {
        0 or 1 or 2 or 11 or 12 => "disconnected",
        3 => "idle-timeout",
        4 => "logon-timeout",
        5 => "session-replaced",
        7 => "access-denied",
        8 => "security-mode-unavailable",
        9 => "insufficient-privileges",
        10 => "fresh-credentials-required",
        _ => "unexpected-disconnect"
    };

    public static string Logon(int error) => error switch
    {
        -7 => "The server refused to disconnect the competing session. Review active sessions or ask an administrator to resolve the contention.",
        -6 => "Windows displayed a no-permissions dialog. Verify Remote Desktop logon rights and the target's session policy.",
        -5 => "Windows displayed session-contention options because another session is using this account or target.",
        -4 => "Windows displayed reconnect options for an existing remote session.",
        -3 => "Windows ended the interrupted sign-in without displaying another prompt.",
        -2 => "Windows is continuing the remote sign-in process.",
        -1 => "The server denied access. Verify Remote Desktop logon rights and target policy.",
        0 => "The username or password was not accepted. Re-enter credentials in the Windows prompt; repeated attempts may lock the account.",
        1 => "The password has expired or must be updated before Remote Desktop sign-in can continue. Use your approved password-change path, then reconnect.",
        2 => "Windows reported another sign-in or post-sign-in error and may display an additional logon screen.",
        3 => "Windows displayed an important sign-in warning. Review that dialog before continuing.",
        unchecked((int)0xC000006D) => "The username or authentication information was not accepted. Re-enter credentials in the Windows prompt.",
        unchecked((int)0xC000006E) => "The credentials are valid, but an account restriction blocked sign-in. Check permitted logon hours and account policy.",
        unchecked((int)0xC0000071) => "The password has expired. Update it through your approved password-change path, then reconnect.",
        unchecked((int)0xC0000072) => "The account is disabled. Contact an administrator before trying again.",
        unchecked((int)0xC0000193) => "The account has expired. Contact an administrator before trying again.",
        unchecked((int)0xC0000224) => "The password must be changed before sign-in can continue. Use your approved password-change path, then reconnect.",
        unchecked((int)0xC0000234) => "The account is locked after repeated failed attempts. Stop retrying and contact an administrator or wait for the lockout period to expire.",
        _ => $"Windows rejected or interrupted RDP sign-in (logon error 0x{unchecked((uint)error):X8}). Verify the username, account state, NLA compatibility, and Remote Desktop logon rights."
    };

    public static string LogonCategory(int error) => error switch
    {
        -7 or -6 or -5 => "session-contention",
        -4 or -3 or -2 => "logon-in-progress",
        -1 => "access-denied",
        0 or unchecked((int)0xC000006D) => "credentials-rejected",
        1 or unchecked((int)0xC0000071) => "password-expired",
        unchecked((int)0xC000006E) => "account-restricted",
        unchecked((int)0xC0000072) => "account-disabled",
        unchecked((int)0xC0000193) => "account-expired",
        unchecked((int)0xC0000224) => "password-change-required",
        unchecked((int)0xC0000234) => "account-locked",
        2 or 3 => "logon-warning",
        _ => "logon-error"
    };

    public static bool IsTerminalLogonError(int error) => error is -1 or 0 or 1 or
        unchecked((int)0xC000006D) or unchecked((int)0xC000006E) or unchecked((int)0xC0000071) or
        unchecked((int)0xC0000072) or unchecked((int)0xC0000193) or unchecked((int)0xC0000224) or
        unchecked((int)0xC0000234);

    public static string Fatal(int error) =>
        $"The Windows RDP client reported a fatal error ({error}). Close this tab, verify the target and local RDP components, then try again.";
}
