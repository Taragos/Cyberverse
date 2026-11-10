module Cyberverse.Ui
import Cyberverse.Network.Managers.*

// Runs UI work once the world's UI can show it, through the delay system (a game job thread, not the network thread).
// Use it for UI and the few engine calls the server triggers; never for per-frame or high-volume work, which stays in
// C++ (see CYB-00009 Progress).
//
// Popups opened too early (while the world still loads or fades in) or from the plugin's network thread turn the cursor
// on but draw nothing (CYB-00009). Ready means both:
// - Codeware's Session/Ready fired for a game in the world, not the main menu (pre-game), and
// - the popup manager (PopupsManager) attached to the player, so popups have a controller to open through, and
// - the game made the player visible after the spawn (PlayerPuppet.OnMakePlayerVisibleAfterSpawn). Session/Ready
//   alone is too early: a popup opened 1.2 s after it got hidden by the end of the loading transition (cursor on, no
//   window, no blur), while one opened 4.6 s after showed.
// Work requested before that waits in a queue; a session end makes it wait again.
public abstract class WorldUiAction extends IScriptable {
    // Called from the delay system with the world's UI ready.
    public func Run(controller: ref<inkGameController>) {}

    // For the log.
    public func Describe() -> String {
        return NameToString(this.GetClassName());
    }
}

public class WorldUi extends ScriptableService {
    private let m_sessionReady: Bool;
    private let m_controller: wref<inkGameController>;
    private let m_playerVisible: Bool;
    private let m_queue: array<ref<WorldUiAction>>;
    // A flush is scheduled on the delay system and hasn't run yet.
    private let m_flushScheduled: Bool;

    public static func Get() -> ref<WorldUi> {
        return GameInstance.GetScriptableServiceContainer().GetService(n"Cyberverse.Ui.WorldUi") as WorldUi;
    }

    public static func Log(message: String) {
        GameInstance.GetNetworkGameSystem().Log("world ui: " + message);
    }

    private cb func OnLoad() {
        let callbacks = GameInstance.GetCallbackSystem();
        callbacks.RegisterCallback(n"Session/Ready", this, n"OnSessionReady");
        callbacks.RegisterCallback(n"Session/BeforeEnd", this, n"OnSessionEnd");
    }

    private cb func OnSessionReady(event: ref<GameSessionEvent>) {
        this.m_sessionReady = !event.IsPreGame();
        WorldUi.Log("session ready, in the world " + ToString(this.m_sessionReady));
        this.ScheduleFlush();
    }

    private cb func OnSessionEnd(event: ref<GameSessionEvent>) {
        WorldUi.Log("session ends, dropping " + ToString(ArraySize(this.m_queue)) + " queued");
        this.m_sessionReady = false;
        this.m_controller = null;
        this.m_playerVisible = false;
        ArrayClear(this.m_queue);
    }

    public func OnPopupsAttached(controller: ref<inkGameController>) {
        this.m_controller = controller;
        WorldUi.Log("popup manager attached, session ready " + ToString(this.m_sessionReady));
        this.ScheduleFlush();
    }

    public func OnPlayerVisible() {
        this.m_playerVisible = true;
        WorldUi.Log("player visible after the spawn, session ready " + ToString(this.m_sessionReady)
            + ", popup manager " + ToString(IsDefined(this.m_controller)));
        this.ScheduleFlush();
    }

    public func IsReady() -> Bool {
        return this.m_sessionReady && IsDefined(this.m_controller) && this.m_playerVisible;
    }

    // Runs the action once ready, from the delay system; may be called from any thread.
    public func WhenReady(action: ref<WorldUiAction>) {
        ArrayPush(this.m_queue, action);
        if this.IsReady() {
            this.ScheduleFlush();
        } else {
            WorldUi.Log("waiting with " + action.Describe() + " (session ready " + ToString(this.m_sessionReady)
                + ", popup manager " + ToString(IsDefined(this.m_controller)) + ", player visible "
                + ToString(this.m_playerVisible) + ")");
        }
    }

    // The delay system calls back from the game's job threads, also when asked from the network thread.
    private func ScheduleFlush() {
        if !this.IsReady() || this.m_flushScheduled || ArraySize(this.m_queue) == 0 {
            return;
        }

        this.m_flushScheduled = true;
        GameInstance.GetDelaySystem(GetGameInstance()).DelayCallback(new WorldUiFlush(), 0.0, false);
    }

    public func Flush() {
        this.m_flushScheduled = false;
        if !this.IsReady() {
            return; // the session ended meanwhile
        }

        let actions = this.m_queue;
        ArrayClear(this.m_queue);
        for action in actions {
            WorldUi.Log("running " + action.Describe());
            action.Run(this.m_controller);
        }
    }
}

public class WorldUiFlush extends DelayCallback {
    public func Call() {
        WorldUi.Get().Flush();
    }
}

// Codeware's popup manager starts with the player, also in the main menu; there it isn't ours to use.
@wrapMethod(PopupsManager)
protected cb func OnPlayerAttach(playerPuppet: ref<GameObject>) -> Bool {
    let result = wrappedMethod(playerPuppet);
    if !GameInstance.GetSystemRequestsHandler().IsPreGame() {
        WorldUi.Get().OnPopupsAttached(this);
    }
    return result;
}

@wrapMethod(PlayerPuppet)
protected cb func OnMakePlayerVisibleAfterSpawn(evt: ref<EndGracePeriodAfterSpawn>) -> Bool {
    let result = wrappedMethod(evt);
    WorldUi.Get().OnPlayerVisible();
    return result;
}

