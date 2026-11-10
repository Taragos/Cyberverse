module Cyberverse.Characters
import Codeware.UI.*
import Cyberverse.Network.Managers.*
import Cyberverse.Ui.*

// The character select and create popups (CYB-00009). The server sends CharacterList once the game has loaded the
// world; the player picks, creates or deletes a character here, and CharacterSpawned closes the popups, applies the
// character's look (or opens the editor for a character without one, see AppearanceEditor.reds) and moves the player to
// where the character was. The server's packets arrive on the plugin's network thread, so everything they
// trigger goes through WorldUi, which also waits until the world's UI can draw popups.

public class CharacterSelection extends ScriptableService {
    private let m_controller: wref<inkGameController>;
    private let m_popup: ref<CharacterPopup>;
    // A list arrived and no character is played yet: the select popup should be open.
    private let m_choosing: Bool;
    // Where Play moves the character once its look is done.
    private let m_pendingTeleport: Bool;
    private let m_pendingPosition: Vector4;
    private let m_pendingYaw: Float;

    public static func Get() -> ref<CharacterSelection> {
        return GameInstance.GetScriptableServiceContainer()
            .GetService(n"Cyberverse.Characters.CharacterSelection") as CharacterSelection;
    }

    public static func Log(message: String) {
        GameInstance.GetNetworkGameSystem().Log("characters: " + message);
    }

    private cb func OnLoad() {
        GameInstance.GetCallbackSystem().RegisterCallback(n"Session/BeforeEnd", this, n"OnSessionEnd");
    }

    // The popups go with the world; after the next load the server sends the list again if still needed.
    private cb func OnSessionEnd(event: ref<GameSessionEvent>) {
        this.m_popup = null;
        this.m_controller = null;
        this.m_choosing = false;
        this.m_pendingTeleport = false;
    }

    // From the plugin, on its network thread.
    public func OnList() {
        this.m_choosing = true;
        WorldUi.Get().WhenReady(new CharacterSelectShow());
    }

    public func OnFailed(message: String) {
        WorldUi.Get().WhenReady(CharacterRequestFailedShow.Create(message));
    }

    public func OnSpawned(name: String, teleport: Bool, position: Vector4, yaw: Float, appearance: String) {
        this.m_choosing = false;
        WorldUi.Get().WhenReady(CharacterSpawnedApply.Create(name, teleport, position, yaw, appearance));
    }

    // From here on, from WorldUi with the world's UI ready.
    public func ShowFailed(message: String) {
        CharacterSelection.Log("refused: " + message);
        if IsDefined(this.m_popup) {
            this.m_popup.ShowError(message);
        }
    }

    // Applies the look first: the hidden editor doesn't load during the teleport's loading screen, and this way its
    // short menu background runs straight into that loading screen. A character without a look gets the editor on
    // screen; it teleports once the player confirms or backs out.
    public func Spawn(name: String, teleport: Bool, position: Vector4, yaw: Float, appearance: String) {
        CharacterSelection.Log("playing " + name + (teleport ? ", moving to its saved place" : "")
            + (StrLen(appearance) == 0 ? ", no look yet" : ""));
        this.ClosePopup();
        this.m_pendingTeleport = teleport;
        this.m_pendingPosition = position;
        this.m_pendingYaw = yaw;
        AppearanceEditor.Get().Start(appearance);
    }

    // From AppearanceEditor once the look is applied, stored or skipped.
    public func OnAppearanceDone() {
        if !this.m_pendingTeleport {
            return;
        }
        this.m_pendingTeleport = false;

        let game = GetGameInstance();
        let player = GetPlayer(game);
        if !IsDefined(player) {
            CharacterSelection.Log("no player to move to the saved place");
            return;
        }

        let rotation: EulerAngles;
        rotation.Yaw = this.m_pendingYaw;
        GameInstance.GetTeleportationFacility(game).Teleport(player, this.m_pendingPosition, rotation);
    }

    public func ShowSelect(controller: ref<inkGameController>) {
        this.m_controller = controller;
        this.ShowSelect();
    }

    public func ShowSelect() {
        if !this.m_choosing {
            return; // a character got picked meanwhile
        }

        let network = GameInstance.GetNetworkGameSystem();
        let ids: array<String>;
        let names: array<String>;
        let count = network.GetCharacterCount();
        let i = 0;
        while i < count {
            ArrayPush(ids, network.GetCharacterId(i));
            ArrayPush(names, network.GetCharacterName(i));
            i += 1;
        }
        CharacterSelection.Log("showing the list, " + ToString(count) + " characters");

        // One window that swaps its pages: closing one popup and opening another in the same moment left none on
        // screen, as the closing one reset the UI state after the new one had opened.
        if IsDefined(this.m_popup) {
            this.m_popup.ShowList(ids, names, network.GetMaxCharacters());
            return;
        }

        if IsDefined(this.m_controller) {
            this.m_popup = CharacterPopup.Create(ids, names, network.GetMaxCharacters());
            this.m_popup.Open(this.m_controller);
        }
    }

    private func ClosePopup() {
        if IsDefined(this.m_popup) {
            this.m_popup.Close();
            this.m_popup = null;
        }
    }
}

public class CharacterSelectShow extends WorldUiAction {
    public func Run(controller: ref<inkGameController>) {
        CharacterSelection.Get().ShowSelect(controller);
    }
}

public class CharacterRequestFailedShow extends WorldUiAction {
    private let m_message: String;

    public func Run(controller: ref<inkGameController>) {
        CharacterSelection.Get().ShowFailed(this.m_message);
    }

    public static func Create(message: String) -> ref<CharacterRequestFailedShow> {
        let self = new CharacterRequestFailedShow();
        self.m_message = message;
        return self;
    }
}

public class CharacterSpawnedApply extends WorldUiAction {
    private let m_name: String;
    private let m_teleport: Bool;
    private let m_position: Vector4;
    private let m_yaw: Float;
    private let m_appearance: String;

    public func Run(controller: ref<inkGameController>) {
        CharacterSelection.Get().Spawn(this.m_name, this.m_teleport, this.m_position, this.m_yaw, this.m_appearance);
    }

    public static func Create(name: String, teleport: Bool, position: Vector4, yaw: Float, appearance: String)
        -> ref<CharacterSpawnedApply> {
        let self = new CharacterSpawnedApply();
        self.m_name = name;
        self.m_teleport = teleport;
        self.m_position = position;
        self.m_yaw = yaw;
        self.m_appearance = appearance;
        return self;
    }
}

// The character window: the list of characters (Play, Delete, New character) or the create form. Escape doesn't close
// it, as the player has to pick a character to play.
public class CharacterPopup extends InGamePopup {
    protected let m_header: wref<InGamePopupHeader>;
    protected let m_footer: wref<InGamePopupFooter>;
    protected let m_column: wref<inkVerticalPanel>;
    protected let m_status: wref<inkText>;
    // A request is on its way; ignore clicks until the server answers.
    protected let m_busy: Bool;
    protected let m_creating: Bool;

    // The list page.
    protected let m_ids: array<String>;
    protected let m_names: array<String>;
    protected let m_max: Int32;
    // The character whose Delete was clicked once and now asks to be clicked again; -1 for none.
    protected let m_confirmDelete: Int32;
    protected let m_deleteButtons: array<ref<SimpleButton>>;

    // The create page.
    protected let m_firstName: ref<HubTextInput>;
    protected let m_lastName: ref<HubTextInput>;

    protected cb func OnInitialize() {
        super.OnInitialize();
        this.m_closeAction = n"cyberverse_none";
    }

    // Runs twice: from Create, and again when the popup manager attaches it to its own root widget.
    protected cb func OnCreate() {
        super.OnCreate();

        let header = InGamePopupHeader.Create();
        header.SetFluffLeft("Cyberverse");
        header.Reparent(this.GetContainerWidget());
        this.m_header = header;

        let footer = InGamePopupFooter.Create();
        footer.Reparent(this.GetContainerWidget());
        this.m_footer = footer;

        let content = InGamePopupContent.Create();
        content.Reparent(this.GetContainerWidget());

        let column = new inkVerticalPanel();
        column.SetName(n"column");
        column.SetChildMargin(new inkMargin(0.0, 0.0, 0.0, 24.0));
        column.Reparent(content.GetRootCompoundWidget());
        this.m_column = column;

        this.Render();
    }

    protected cb func OnShown() {
        // The footer offers "Close", which this window doesn't do.
        this.m_footer.GetHints().ClearButtonHints();
        CharacterSelection.Log("window shown");
    }

    public func ShowList(ids: array<String>, names: array<String>, max: Int32) {
        this.m_ids = ids;
        this.m_names = names;
        this.m_max = max;
        this.m_creating = false;
        this.m_busy = false;
        this.Render();
    }

    public func ShowCreate() {
        this.m_creating = true;
        this.m_busy = false;
        this.Render();
    }

    public func ShowError(message: String) {
        this.m_busy = false;
        this.SetStatus(message, new HDRColor(1.0, 0.35, 0.3, 1.0));
    }

    protected func ShowWaiting(message: String) {
        this.m_busy = true;
        this.SetStatus(message, new HDRColor(1.0, 0.9, 0.2, 1.0));
    }

    protected func ShowHint(message: String) {
        this.SetStatus(message, new HDRColor(1.0, 0.9, 0.2, 1.0));
    }

    private func SetStatus(message: String, color: HDRColor) {
        this.m_status.SetText(message);
        this.m_status.SetTintColor(color);
    }

    private func Render() {
        if !IsDefined(this.m_column) {
            return;
        }

        this.m_column.RemoveAllChildren();
        ArrayClear(this.m_deleteButtons);
        this.m_confirmDelete = -1;
        if this.m_creating {
            this.RenderCreate();
        } else {
            this.RenderList();
        }
    }

    private func RenderList() {
        this.m_header.SetTitle("Pick a character");
        let i = 0;
        while i < ArraySize(this.m_ids) {
            let row = new inkHorizontalPanel();
            row.SetChildMargin(new inkMargin(0.0, 0.0, 30.0, 0.0));
            row.Reparent(this.m_column);

            let name = CharacterPopup.Text(this.m_names[i], 48);
            name.SetSize(new Vector2(520.0, 80.0));
            name.SetVAlign(inkEVerticalAlign.Center);
            name.Reparent(row);

            this.AddButton(row, StringToName("play_" + ToString(i)), "Play");
            ArrayPush(this.m_deleteButtons, this.AddButton(row, StringToName("delete_" + ToString(i)), "Delete"));
            i += 1;
        }

        if ArraySize(this.m_ids) < this.m_max {
            this.AddButton(this.m_column, n"new", "New character");
        }

        let slots = ToString(ArraySize(this.m_ids)) + " of " + ToString(this.m_max) + " characters.";
        this.AddStatus(ArraySize(this.m_ids) == 0 ? "Create your first character. " + slots : slots);
    }

    private func RenderCreate() {
        this.m_header.SetTitle("New character");
        this.m_firstName = CharacterPopup.Input(n"firstName", "First name");
        this.m_firstName.Reparent(this.m_column);
        this.m_lastName = CharacterPopup.Input(n"lastName", "Last name");
        this.m_lastName.Reparent(this.m_column);

        let buttons = new inkHorizontalPanel();
        buttons.SetChildMargin(new inkMargin(0.0, 0.0, 30.0, 0.0));
        buttons.Reparent(this.m_column);
        this.AddButton(buttons, n"create", "Create");
        this.AddButton(buttons, n"back", "Back");

        this.AddStatus("Each name: 2 to 16 letters; ' and - are fine too.");
    }

    private func AddStatus(text: String) {
        let status = CharacterPopup.Text(text, 38);
        status.SetName(n"status");
        status.Reparent(this.m_column);
        this.m_status = status;
    }

    private func AddButton(parent: ref<inkCompoundWidget>, name: CName, label: String) -> ref<SimpleButton> {
        let button = SimpleButton.Create();
        button.SetName(name);
        button.SetText(label);
        button.Reparent(parent);
        button.RegisterToCallback(n"OnBtnClick", this, n"OnButtonClick");
        return button;
    }

    protected cb func OnButtonClick(widget: wref<inkWidget>) -> Bool {
        if !this.m_busy {
            let name = NameToString(widget.GetName());
            CharacterSelection.Log("clicked " + name);
            if this.m_creating {
                this.OnCreateButton(name);
            } else {
                this.OnListButton(name);
            }
        }
        return true;
    }

    private func OnListButton(name: String) {
        if Equals(name, "new") {
            this.ShowCreate();
            return;
        }

        let index = StringToInt(StrAfterFirst(name, "_"));
        if index < 0 || index >= ArraySize(this.m_ids) {
            return;
        }

        if StrBeginsWith(name, "play_") {
            if GameInstance.GetNetworkGameSystem().SelectCharacter(this.m_ids[index]) {
                this.ShowWaiting("Loading " + this.m_names[index] + "...");
            } else {
                this.ShowError("Not connected to the server.");
            }
            return;
        }

        if StrBeginsWith(name, "delete_") {
            if this.m_confirmDelete != index {
                if this.m_confirmDelete >= 0 {
                    this.m_deleteButtons[this.m_confirmDelete].SetText("Delete");
                }
                this.m_confirmDelete = index;
                this.m_deleteButtons[index].SetText("Sure?");
                this.ShowHint("Click Sure? to delete " + this.m_names[index] + ".");
                return;
            }

            if GameInstance.GetNetworkGameSystem().DeleteCharacter(this.m_ids[index]) {
                this.ShowWaiting("Deleting " + this.m_names[index] + "...");
            } else {
                this.ShowError("Not connected to the server.");
            }
        }
    }

    private func OnCreateButton(name: String) {
        if Equals(name, "back") {
            this.ShowList(this.m_ids, this.m_names, this.m_max);
            return;
        }

        // The server trims and checks the names, and answers with the new list or why not.
        if Equals(name, "create") {
            let first = this.m_firstName.GetText();
            let last = this.m_lastName.GetText();
            if StrLen(first) == 0 || StrLen(last) == 0 {
                this.ShowError("Enter a first and a last name.");
                return;
            }

            if GameInstance.GetNetworkGameSystem().CreateCharacter(first, last) {
                this.ShowWaiting("Creating " + first + " " + last + "...");
            } else {
                this.ShowError("Not connected to the server.");
            }
        }
    }

    public func UseCursor() -> Bool {
        return true;
    }

    public static func Text(text: String, size: Int32) -> ref<inkText> {
        let label = new inkText();
        label.SetFontFamily("base\\gameplay\\gui\\fonts\\raj\\raj.inkfontfamily");
        label.SetFontStyle(n"Medium");
        label.SetFontSize(size);
        label.SetTintColor(new HDRColor(1.0, 0.9, 0.2, 1.0));
        label.SetText(text);
        return label;
    }

    private static func Input(name: CName, placeholder: String) -> ref<HubTextInput> {
        let input = HubTextInput.Create();
        input.SetName(name);
        input.SetDefaultText(placeholder);
        input.SetMaxLength(16);
        input.SetWidth(700.0);
        return input;
    }

    public static func Create(ids: array<String>, names: array<String>, max: Int32) -> ref<CharacterPopup> {
        let self = new CharacterPopup();
        self.m_ids = ids;
        self.m_names = names;
        self.m_max = max;
        self.CreateInstance();
        return self;
    }
}
