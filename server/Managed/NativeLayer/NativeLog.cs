using NLog;

namespace Cyberverse.Server.NativeLayer;

/// <summary>Sends the native library's log lines (and GameNetworkingSockets' warnings) to NLog, logger "Native".</summary>
internal static class NativeLog
{
    private static readonly Logger Logger = LogManager.GetLogger("Native");

    // Kept in a static field: the native side holds only the function pointer, so the delegate must never be collected.
    private static readonly Native.LogCallback Callback = OnLog;

    public static void Install() => Native.server_set_log_callback(Callback);

    public static LogLevel ToLevel(int nativeLevel) => nativeLevel switch
    {
        1 => LogLevel.Debug,
        2 => LogLevel.Info,
        3 => LogLevel.Warn,
        _ => LogLevel.Error,
    };

    // May run on GameNetworkingSockets' thread; NLog is thread-safe. Nothing may throw back into native code.
    private static void OnLog(int level, string message)
    {
        try
        {
            Logger.Log(ToLevel(level), message);
        }
        catch
        {
            // logging failed; nowhere left to report it
        }
    }
}
