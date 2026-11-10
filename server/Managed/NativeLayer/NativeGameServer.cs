namespace Cyberverse.Server.NativeLayer;

public class NativeGameServer
{
    private static bool _hadSuccessfulNativeInit = false;
    private readonly ushort _listeningPort;
    private readonly IntPtr _nativePtr;
    private readonly Native.ConnectionStateChangedCallback _connectionStateChangedCallbackDelegate;
    
    public NativeGameServer(ushort listeningPort)
    {
        if (!_hadSuccessfulNativeInit)
        {
            NativeLog.Install();
            if (!Native.init_gameserver())
            {
                throw new SystemException("Failed to initialize native library");
            }

            _hadSuccessfulNativeInit = true;
        }

        _listeningPort = listeningPort;
        _nativePtr = Native.server_create(listeningPort);
        // TODO: Apparently this is required to prevent the delegate from being GC'ed, so double check and ignore the warnings?
        _connectionStateChangedCallbackDelegate = new Native.ConnectionStateChangedCallback(OnConnectionStateChange);
        Native.server_set_connection_state_changed_cb(_nativePtr, _connectionStateChangedCallbackDelegate);
    }

    public virtual void Update(float deltaTime)
    {
        Native.server_update(_nativePtr, deltaTime);
    }

    public void RunBlocking()
    {
        Native.server_run_blocking(_nativePtr);
    }

    /// <summary>The next received message. Pass every non-empty one to <see cref="ReleaseMessage"/>.</summary>
    public Message PollIncomingMessage()
    {
        return Native.server_poll_message(_nativePtr);
    }

    public static void ReleaseMessage(Message message)
    {
        Native.message_release(message.Handle);
    }

    /// <summary>Sends a whole message (frame header and packet) as is.</summary>
    public unsafe bool SendRaw(uint connectionId, ReadOnlySpan<byte> data, bool reliable)
    {
        fixed (byte* ptr = data)
        {
            return Native.server_send_message(_nativePtr, connectionId, ptr, (uint)data.Length,
                reliable ? SendFlags.Reliable : SendFlags.Unreliable);
        }
    }

    /// <summary>
    /// Closes the connection once the reliable packets already sent to it have arrived. reason is an application end
    /// reason (from 1000 up). No <see cref="OnConnectionStateChange"/> follows.
    /// </summary>
    public virtual bool CloseConnection(uint connectionId, int reason, string debug)
    {
        return Native.server_close_connection(_nativePtr, connectionId, reason, debug);
    }

    protected virtual void OnConnectionStateChange(ESteamNetworkingConnectionState state, uint connectionId)
    {
    }
}