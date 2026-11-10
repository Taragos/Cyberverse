module Cyberverse.Characters
import Cyberverse.Network.Managers.*

// A character's look (CYB-00009), made and applied with the game's own in-world editor, the one mirrors open
// (MenuScenario_CharacterCustomizationMirror, characterCreationBodyMorphMenu). Only that editor has the full option list:
// in the world the customization system loads the first-person body's options only (no head, so no hair, face or eyes).
//
// - A character without a look: Play opens the editor on screen; Confirm stores the look on the server, Back keeps the
//   start save's look and the editor opens again on the next Play.
// - A character with a look: Play opens the editor without showing it, sets the stored options and confirms. The game
//   shows its menu background for about 1.3 s meanwhile.
// Either way CharacterSelection.OnAppearanceDone follows, which moves the character to its saved place.
//
// Encoding: "v1 body=m name=value name=value ...", the editable options in use (about 50, some 700 bytes), in the
// editor's own order, so a hairstyle comes before its color option. Body type: the start save's for now (CYB-00027).
public class AppearanceEditor extends ScriptableService {
    // An editor of ours is open or about to open.
    private let m_active: Bool;

    public static func Get() -> ref<AppearanceEditor> {
        return GameInstance.GetScriptableServiceContainer()
            .GetService(n"Cyberverse.Characters.AppearanceEditor") as AppearanceEditor;
    }

    public static func Log(message: String) {
        GameInstance.GetNetworkGameSystem().Log("appearance: " + message);
    }

    private cb func OnLoad() {
        GameInstance.GetCallbackSystem().RegisterCallback(n"Session/BeforeEnd", this, n"OnSessionEnd");
    }

    private cb func OnSessionEnd(event: ref<GameSessionEvent>) {
        this.m_active = false;
    }

    // From CharacterSelection on Play, with the world's UI ready.
    public func Start(appearance: String) {
        let data = new CyberverseMorphMenuUserData();
        data.m_editMode = gameuiCharacterCustomizationEditTag.Ripperdoc;
        data.m_apply = appearance;
        data.m_show = StrLen(appearance) == 0;
        this.m_active = true;
        // Not in the same moment as the character window closes: a menu opened then didn't show (CYB-00009, probe 6).
        GameInstance.GetDelaySystem(GetGameInstance()).DelayCallback(AppearanceEditorOpen.Create(data), 0.0, false);
    }

    // The editor opens through a menu event to the current scenario. Shown or not, the game stays in gameplay mode (HUD
    // and key hints on screen) and draws only the editor's background, until the player presses Escape, which the
    // engine handles and which switches to menu mode; then the editor works. None of these switched it (CYB-00009):
    // ShowMenus, an inkMenuInstance_SpawnEvent through UISystem (before and after loading), inkMenuLayer_SetMenuModeEvent,
    // the in-game menu controller's SpawnMenuInstanceDataEvent. UISystem.RequestVendorMenu with the mirror scenario
    // showed it at once, but options couldn't be changed and it couldn't be left. Until a proper way is found, a HUD
    // message asks for Escape.
    // Hidden, applying a stored look: the same, and staying in gameplay mode is what's wanted there.
    public func Open(data: ref<CyberverseMorphMenuUserData>) {
        if data.m_show {
            AppearanceEditor.Log("opening the editor; the player presses Escape to see it");
            AppearanceEditor.ShowHint("Press Esc to open the character editor");
        } else {
            AppearanceEditor.Log("applying the stored look");
        }
        GameInstance.GetUISystem(GetGameInstance()).QueueMenuEvent(n"OnCyberverseOpenAppearanceEditor", data);
    }

    // An empty text hides the message.
    public static func ShowHint(text: String) {
        let message: SimpleScreenMessage;
        message.isShown = StrLen(text) > 0;
        message.isInstant = true;
        message.duration = 60.0;
        message.message = text;
        let notifications = GetAllBlackboardDefs().UI_Notifications;
        GameInstance.GetBlackboardSystem(GetGameInstance()).Get(notifications)
            .SetVariant(notifications.OnscreenMessage, ToVariant(message), true);
    }

    // The editor saved the look onto the player (ReFinalizeState) or was left with Back.
    public func OnFinished(saved: Bool) {
        if !this.m_active {
            return; // a mirror in the world, not ours
        }
        this.m_active = false;
        AppearanceEditor.ShowHint("");
        AppearanceEditor.Log(saved ? "look saved on the player" : "editor left without saving");
        CharacterSelection.Get().OnAppearanceDone();
    }

    // The options the editor has loaded, as stored on the server.
    public static func Encode() -> String {
        let system = GameInstance.GetCharacterCustomizationSystem(GetGameInstance());
        let state = system.GetState();
        let look = "v1 body=" + (IsDefined(state) && !state.IsBodyGenderMale() ? "f" : "m");
        let options = system.GetUnitedOptions(true, true, true); // a local first: ArraySize on the call's result is 0
        for option in options {
            if IsDefined(option.info) && option.isEditable && option.currIndex != 4294967295u {
                look += " " + NameToString(option.info.name) + "=" + ToString(option.currIndex);
            }
        }
        return look;
    }

    // Sets the stored options on the editor's options; returns how many it changed. Unknown names are skipped (an
    // option the game dropped, or one of the other body type).
    public static func Apply(look: String) -> Int32 {
        let system = GameInstance.GetCharacterCustomizationSystem(GetGameInstance());
        let names: array<String>;
        let values: array<Uint32>;
        for pair in StrSplit(look, " ") {
            let name: String;
            let value: String;
            if StrSplitFirst(pair, "=", name, value) && NotEquals(name, "body") {
                ArrayPush(names, name);
                ArrayPush(values, Cast<Uint32>(StringToInt(value)));
            }
        }

        let changed = 0;
        let options = system.GetUnitedOptions(true, true, true);
        for option in options {
            if IsDefined(option.info) {
                let i = ArrayFindFirst(names, NameToString(option.info.name));
                if i >= 0 && option.currIndex != values[i] {
                    system.ApplyChangeToOption(option, values[i], true);
                    changed += 1;
                }
            }
        }
        return changed;
    }

    // The stored look's body type against the player's, for the log; the body type isn't changed yet (CYB-00027).
    public static func CheckBody(look: String) {
        let state = GameInstance.GetCharacterCustomizationSystem(GetGameInstance()).GetState();
        let male = !IsDefined(state) || state.IsBodyGenderMale();
        if StrContains(look, male ? "body=f" : "body=m") {
            AppearanceEditor.Log("the stored look is for the other body type; applying what fits");
        }
    }
}

public class CyberverseMorphMenuUserData extends MorphMenuUserData {
    // The stored look to apply, empty for a new one.
    public let m_apply: String;
    // Show the editor; otherwise it stays hidden, applies m_apply and confirms.
    public let m_show: Bool;
}

public class AppearanceEditorOpen extends DelayCallback {
    private let m_data: ref<CyberverseMorphMenuUserData>;

    public static func Create(data: ref<CyberverseMorphMenuUserData>) -> ref<AppearanceEditorOpen> {
        let self = new AppearanceEditorOpen();
        self.m_data = data;
        return self;
    }

    public func Call() {
        AppearanceEditor.Get().Open(this.m_data);
    }
}

// No game script opens the mirror editor (the engine does, from a mirror); this lets ours be asked for through
// UISystem.QueueMenuEvent while the player is in the world.
@addMethod(MenuScenario_Idle)
protected cb func OnCyberverseOpenAppearanceEditor(userData: ref<IScriptable>) -> Bool {
    this.SwitchToScenario(n"MenuScenario_CharacterCustomizationMirror", userData);
}

@addField(characterCreationBodyMorphMenu)
private let m_cyberverse: ref<CyberverseMorphMenuUserData>;

@wrapMethod(characterCreationBodyMorphMenu)
protected cb func OnSetUserData(userData: ref<IScriptable>) -> Bool {
    this.m_cyberverse = userData as CyberverseMorphMenuUserData;
    return wrappedMethod(userData);
}

// The options are loaded with the character's current values; a hidden editor applies the stored look and confirms.
@wrapMethod(characterCreationBodyMorphMenu)
protected cb func OnInitializeOptionsList(evt: ref<gameuiCharacterCustomizationSystem_OnInitializeOptionsListEvent>) -> Bool {
    let result = wrappedMethod(evt);
    if IsDefined(this.m_cyberverse) && !this.m_cyberverse.m_show {
        let look = this.m_cyberverse.m_apply;
        AppearanceEditor.CheckBody(look);
        // A hairstyle switches which color option is in use; a second pass catches values that only fit then.
        let pass = 1;
        while pass <= 3 {
            let changed = AppearanceEditor.Apply(look);
            AppearanceEditor.Log("pass " + ToString(pass) + " changed " + ToString(changed) + " options");
            if changed == 0 {
                break;
            }
            pass += 1;
        }
        this.ConfirmCustomizedCharacter();
    }
    return result;
}

// Confirm on screen: store the new look. Also for a mirror in the world, so the server keeps what the player sees.
@wrapMethod(characterCreationBodyMorphMenu)
public final func ConfirmCustomizedCharacter() {
    if this.m_updatingFinalizedState && (!IsDefined(this.m_cyberverse) || this.m_cyberverse.m_show) {
        let look = AppearanceEditor.Encode();
        AppearanceEditor.Log("storing the look (" + ToString(StrLen(look)) + " bytes)");
        GameInstance.GetNetworkGameSystem().SendCharacterAppearance(look);
    }
    wrappedMethod();
}

@wrapMethod(characterCreationBodyMorphMenu)
protected cb func OnReFinalizeComplete(evt: ref<gameuiCharacterCustomizationSystem_OnReFinalizeStateCompleteEvent>) -> Bool {
    let result = wrappedMethod(evt);
    AppearanceEditor.Get().OnFinished(true);
    return result;
}

@wrapMethod(characterCreationBodyMorphMenu)
protected cb func OnCancelFinalizedStateUpdate(evt: ref<gameuiCharacterCustomizationSystem_OnCancelFinalizedStateUpdateEvent>) -> Bool {
    let result = wrappedMethod(evt);
    AppearanceEditor.Get().OnFinished(false);
    return result;
}
