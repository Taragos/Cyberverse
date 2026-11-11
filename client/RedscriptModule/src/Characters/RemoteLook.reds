module Cyberverse.Characters
import Cyberverse.Network.Managers.*

// Other players' V with their own look (CYB-00029). A remote player spawns as our own record (RemoteLook.Record,
// CYB-00032): the game's cutscene V without impostor, with V's body, minus the customization controllers that copied
// the local player's look. Without TweakXL it falls back to the game's record (RemoteLook.FallbackRecord), whose
// controllers the passes below still turn off. The plugin resolves the stored look
// through the look table (NetworkGameSystem.ResolveLook) into parts (which mesh, with which appearance, bound to what)
// and face shapes. Two passes put it on the puppet:
//
// - While the engine builds the puppet (RemoteLookSpawn): the look's hair, beard, piercings, body etc. are added, as
//   the game's own parts, which the plugin has the engine extract from the game's .app files
//   (NetworkGameSystem.HairParts, LookPart.atSpawn); and
//   the customization controllers are turned off (they would copy the local player's look).
// - Once the puppet is in the world (RemoteLookApply, stepped by RemoteLookQueue): V's head and arms as items (the way the game's inventory preview
//   does it), whose face parts then land on the puppet; every part and face shape the table knows is cleared, and the
//   look's other parts reuse those existing parts with the look's file and appearance; face shapes are set.

// One part a look puts on V: kind S skinned mesh, G garment mesh, M morph target mesh, A a hairstyle's skeleton (file
// is its rig, appearance its animation graph). skinnedTo / hangsFrom name other parts; empty when it has none.
public native class LookPart {
    public native let kind: String;
    public native let name: String;
    public native let file: String;
    public native let appearance: String;
    public native let skinnedTo: String;
    public native let hangsFrom: String;
    public native let atSpawn: Bool; // built from the game's .app while the engine builds the puppet (CYB-00032)
}

// A face shape: a morph target in one region of the head.
public native class LookMorph {
    public native let target: String;
    public native let region: String;
}

// A resolved look, or everything the table can change on a body (LookResetList: parts with only a name). loaded is
// false when the plugin has no look table.
public native class LookChanges {
    public native let loaded: Bool;
    public native let body: String;
    public native let parts: array<ref<LookPart>>;
    public native let morphs: array<ref<LookMorph>>;
}

public abstract class RemoteLook {
    // Our own record (client/tweaks/remote_v.yaml, CYB-00032): the cutscene V's record with our template, which has no
    // customization controllers and walks with the NPC animation graph.
    public static func Record(body: String) -> TweakDBID {
        return Equals(body, "f") ? t"Character.Cyberverse_Remote_V_Female" : t"Character.Cyberverse_Remote_V_Male";
    }

    // The game's cutscene V, which the plugin spawns instead when our record is missing (no TweakXL or archive).
    public static func FallbackRecord(body: String) -> TweakDBID {
        return Equals(body, "f") ? t"Character.TPP_Player_Cutscene_No_Impostor_Female"
                                 : t"Character.TPP_Player_Cutscene_No_Impostor_Male";
    }

    // The body ("m" or "f") of a stored look; "m" when it says nothing.
    public static func Body(look: String) -> String {
        return StrContains(look, "body=f") ? "f" : "m";
    }

    public static func Apply(id: EntityID, look: String) {
        RemoteLookQueue.Get().Add(id, look);
    }

    // Parts built at spawn: every part the table has a game .app appearance for, i.e. all but what the head and arms
    // items bring (head, eyes, eyebrows, eyelashes, teeth, arms): hair and its skeleton, beard, piercings, face
    // cyberware, makeup, the body and its tattoos, genitals and scars (CYB-00032). The second pass reuses the rest.
    public static func BuiltAtSpawn(part: ref<LookPart>) -> Bool {
        return part.atSpawn;
    }

    // Our name for a part we build, so it can't clash with a part of the template or of the local look.
    public static func OwnName(name: String) -> String {
        return "cyberverse_" + name;
    }

    // What a part is bound to: our own skeleton when the look brings one by that name, "root" when it names none.
    public static func Target(name: String, skeletons: array<String>) -> String {
        if StrLen(name) == 0 {
            return "root";
        }
        return ArrayContains(skeletons, name) ? RemoteLook.OwnName(name) : name;
    }

    // The names of the look's skeleton parts (a hairstyle's hair_dangle).
    public static func Skeletons(changes: ref<LookChanges>) -> array<String> {
        let names: array<String>;
        for part in changes.parts {
            if Equals(part.kind, "A") {
                ArrayPush(names, part.name);
            }
        }
        return names;
    }

    // A part's category: its name up to the first "_" (hh hair, hb beard, he eyes, hx makeup, i0/i1 piercings, ...).
    public static func Category(name: String) -> String {
        let head: String;
        let rest: String;
        return StrSplitFirst(name, "_", head, rest) ? head : name;
    }

    public static func Has(entity: ref<Entity>, name: String) -> Bool {
        let wanted = StringToName(name);
        for component in entity.GetComponents() {
            if Equals(component.GetName(), wanted) {
                return true;
            }
        }
        return false;
    }

    public static func TurnOffCustomization(entity: ref<Entity>) {
        for component in entity.GetComponents() {
            if StrBeginsWith(NameToString(component.GetClassName()), "gameuiCharacterCustomization") {
                component.Toggle(false);
            }
        }
    }
}

// The first pass. The hair goes on at Codeware's Entity/Extract: the engine has read the puppet's template and is about
// to assemble the entity from its parts, and parts added to the template here are assembled and set up exactly like
// its own. (On a living entity they aren't set up at all: no owner, no skinning, no pose.) The look is there by then:
// the plugin holds a player's spawn until its look has arrived (NetworkGameSystem.LookOf). Both callbacks may run on a
// job thread: they touch only this entity and the plugin's locked look store.
//
// Known defect: a mesh the game binds to the body ("root") although its vertices hang on bones of the hairstyle's
// skeleton stays in its rest pose, e.g. the ring of the ponytail (hh_083). On the game's own characters such a mesh
// reaches those bones; here it doesn't, whichever way the parts are made or added (see CYB-00029, 2026-11-11), also on
// our own template (CYB-00032; it only looked right while that puppet stood in its bind pose).
public class RemoteLookSpawn extends ScriptableService {
    private cb func OnLoad() {
        GameInstance.GetCallbackSystem().RegisterCallback(n"Entity/Extract", this, n"OnExtract")
            .AddTarget(EntityTarget.RecordID(RemoteLook.Record("m")))
            .AddTarget(EntityTarget.RecordID(RemoteLook.Record("f")))
            .AddTarget(EntityTarget.RecordID(RemoteLook.FallbackRecord("m")))
            .AddTarget(EntityTarget.RecordID(RemoteLook.FallbackRecord("f")));
        GameInstance.GetCallbackSystem().RegisterCallback(n"Entity/Initialize", this, n"OnInitialize")
            .AddTarget(DynamicEntityTarget.Tag(n"RED4ext"));
    }

    private cb func OnExtract(event: ref<EntityBuilderEvent>) {
        let builder = event.GetEntityBuilder();
        if !IsDefined(builder) {
            return;
        }
        let network = GameInstance.GetNetworkGameSystem();
        let id = builder.GetEntityID();
        let look = network.LookOf(id);
        if StrLen(look) == 0 {
            return; // not one of ours (the game's own cutscene V), or its look never came
        }
        let template = builder.GetTemplate();
        for component in template.GetComponents() {
            if StrBeginsWith(NameToString(component.GetName()), RemoteLook.OwnName("")) {
                return; // built already, should the event come twice for one entity
            }
        }
        let added = 0;
        for part in network.HairParts(look) {
            let component = part as IComponent;
            if IsDefined(component) {
                template.AddComponent(component);
                added += 1;
            }
        }
        network.Log(s"remote look at build: \(added) parts for \(EntityID.ToHash(id))");
    }

    // Before the customization controllers ever run: they would copy the local player's look.
    private cb func OnInitialize(event: ref<EntityLifecycleEvent>) {
        let entity = event.GetEntity();
        if IsDefined(entity) && StrLen(GameInstance.GetNetworkGameSystem().LookOf(entity.GetEntityID())) > 0 {
            RemoteLook.TurnOffCustomization(entity);
        }
    }
}

// The looks waiting for their second pass. The plugin steps them every 0.25 s from its own frame update
// (NetworkGameSystem.TickRemoteLooks): the game's delay callbacks get lost when a remote player spawns while the local
// player is still entering the world.
public class RemoteLookQueue extends ScriptableService {
    private let m_pending: array<ref<RemoteLookApply>>;
    // Every remote V with a look, for Reapply.
    private let m_known: array<EntityID>;

    public static func Get() -> ref<RemoteLookQueue> {
        return GameInstance.GetScriptableServiceContainer()
            .GetService(n"Cyberverse.Characters.RemoteLookQueue") as RemoteLookQueue;
    }

    // A look for an entity; replaces one still waiting for it.
    public func Add(id: EntityID, look: String) {
        this.Forget(id);
        ArrayPush(this.m_known, id);
        ArrayPush(this.m_pending, RemoteLookApply.Create(id, look, 0));
    }

    // The local player's look changed (a mirror): the game pushes the new local look into the remote Vs as well, past
    // their switched-off customization controllers (seen 2026-11-11, CYB-00029: B's V got A's new hair on A's screen).
    // The second pass runs again on each of them, 1 s later so the local parts are there to hide.
    public func Reapply() {
        let network = GameInstance.GetNetworkGameSystem();
        for id in this.m_known {
            let look = network.LookOf(id);
            if StrLen(look) > 0 {
                this.ForgetPending(id);
                ArrayPush(this.m_pending, RemoteLookApply.Create(id, look, 4));
            }
        }
        network.Log(s"remote look: local look changed, applying \(ArraySize(this.m_known)) remote looks again");
    }

    // Drops an entity (it's being destroyed).
    public func Forget(id: EntityID) {
        ArrayRemove(this.m_known, id);
        this.ForgetPending(id);
    }

    private func ForgetPending(id: EntityID) {
        let i = 0;
        while i < ArraySize(this.m_pending) {
            if this.m_pending[i].Is(id) {
                ArrayErase(this.m_pending, i);
            } else {
                i += 1;
            }
        }
    }

    public func Tick() {
        let i = 0;
        while i < ArraySize(this.m_pending) {
            if this.m_pending[i].Step() {
                ArrayErase(this.m_pending, i);
            } else {
                i += 1;
            }
        }
    }
}

// The second pass, once the puppet is in the world. Step() returns true when it's done or gave up.
public class RemoteLookApply {
    private let m_id: EntityID;
    private let m_look: String;
    private let m_tries: Int32;
    private let m_gives: Int32;      // how often head and arms were given
    private let m_sinceGiven: Int32; // steps since then, 0 = give now
    private let m_headSeen: Bool;
    private let m_delay: Int32;      // steps to wait before starting

    public static func Create(id: EntityID, look: String, delay: Int32) -> ref<RemoteLookApply> {
        let self = new RemoteLookApply();
        self.m_id = id;
        self.m_look = look;
        self.m_delay = delay;
        return self;
    }

    public func Is(id: EntityID) -> Bool {
        return Equals(this.m_id, id);
    }

    // Not done yet: true when it's time to give up.
    private func Wait(step: String) -> Bool {
        this.m_tries += 1;
        if this.m_tries > 240 { // 60 s: a client still loading the world can take a while to stream the puppet in
            GameInstance.GetNetworkGameSystem().Log(s"remote look: gave up on \(EntityID.ToHash(this.m_id)) waiting for \(step)");
            return true;
        }
        return false;
    }

    public func Step() -> Bool {
        if this.m_delay > 0 {
            this.m_delay -= 1;
            return false;
        }
        let entity = GameInstance.GetDynamicEntitySystem().GetEntity(this.m_id);
        let puppet = entity as GameObject;
        if !IsDefined(puppet) {
            return this.Wait("the puppet to stream in");
        }

        if !entity.IsAttached() {
            return this.Wait("the puppet to attach to the world");
        }

        // V's head and arms are items. A puppet that has only just appeared can lose them without a trace (seen
        // 2026-11-11: the head never reached its slot), so they're given again every 2 s until the head is there.
        let body = RemoteLook.Body(this.m_look);
        let transactions = GameInstance.GetTransactionSystem(GetGameInstance());
        let head = transactions.GetItemInSlot(puppet, t"AttachmentSlots.TppHead");
        if !IsDefined(head) {
            if this.m_sinceGiven == 0 {
                let headItem = ItemID.FromTDBID(Equals(body, "f") ? t"Items.PlayerWaTppHead" : t"Items.PlayerMaTppHead");
                transactions.GiveItem(puppet, headItem, 1);
                transactions.AddItemToSlot(puppet, EquipmentSystem.GetPlacementSlot(headItem), headItem);
                let arms = ItemID.FromTDBID(t"Items.CharacterCustomizationArms");
                transactions.GiveItem(puppet, arms, 1);
                transactions.AddItemToSlot(puppet, EquipmentSystem.GetPlacementSlot(arms), arms);
                this.m_gives += 1;
                if this.m_gives > 1 {
                    GameInstance.GetNetworkGameSystem().Log(s"remote look: head and arms given again to \(EntityID.ToHash(this.m_id)) (\(this.m_gives). time)");
                }
            }
            this.m_sinceGiven = (this.m_sinceGiven + 1) % 8;
            return this.Wait("the head item");
        }

        // The face parts land on the body a moment after the head item is in its slot; give it one more round.
        if !this.m_headSeen {
            this.m_headSeen = true;
            return this.Wait("the head item's parts");
        }

        // Again: the controllers of a puppet that spawned before its look arrived, and the head item's own.
        RemoteLook.TurnOffCustomization(puppet);
        RemoteLook.TurnOffCustomization(head);
        RemoteLookApply.ApplyLook(entity, GameInstance.GetNetworkGameSystem().ResolveLook(this.m_look));
        return true;
    }

    private static func ApplyLook(entity: ref<Entity>, changes: ref<LookChanges>) {
        let network = GameInstance.GetNetworkGameSystem();
        if !changes.loaded {
            network.Log("remote look: no look table, keeping the default V");
            return;
        }
        let skeletons = RemoteLook.Skeletons(changes);

        // Clear: hide every face and makeup part the table can set (and the local look's, should the game have pushed
        // it in), flatten every face shape; what the look needs comes back below. Our own parts built at spawn have
        // other names (RemoteLook.OwnName) and stay. Garments and t0_/a0_ parts stay too.
        let known: array<CName>;
        let reset = network.LookResetList(changes.body);
        for part in reset.parts {
            ArrayPush(known, StringToName(part.name));
        }
        for morph in reset.morphs {
            entity.ApplyMorphTarget(StringToName(morph.target), StringToName(morph.region), 0.0);
        }
        let components = entity.GetComponents();
        let spare: array<ref<IComponent>>;
        for component in components {
            if ArrayContains(known, component.GetName()) && RemoteLookApply.IsReplaceable(component) {
                component.Toggle(false);
                ArrayPush(spare, component);
            }
        }

        let reused = 0;
        let atSpawn = 0;
        let missing = 0;
        for part in changes.parts {
            if RemoteLook.BuiltAtSpawn(part) {
                if RemoteLook.Has(entity, RemoteLook.OwnName(part.name)) {
                    atSpawn += 1;
                } else {
                    missing += 1; // the look arrived after the puppet was built
                }
            } else {
                let component = RemoteLookApply.TakePart(spare, components, part.kind, StringToName(part.name),
                                                         StringToName(RemoteLook.Target(part.skinnedTo, skeletons)));
                if IsDefined(component) {
                    network.ChangePartResource(component, part.file);
                    component.ChangeAppearance(StringToName(part.appearance), true);
                    component.Toggle(true);
                    reused += 1;
                } else {
                    missing += 1;
                    network.Log(s"remote look: no part for \(part.name) (\(part.file))");
                }
            }
        }
        for morph in changes.morphs {
            entity.ApplyMorphTarget(StringToName(morph.target), StringToName(morph.region), 1.0);
        }

        // Face shapes only show after the morph meshes refresh.
        for component in entity.GetComponents() {
            if IsDefined(component as entMorphTargetSkinnedMeshComponent) && component.IsEnabled() {
                component.RefreshAppearance();
            }
        }
        network.Log(s"remote look: \(atSpawn) parts from spawn, \(reused) reused, \(ArraySize(changes.morphs)) face shapes, \(missing) missing (body \(changes.body))");
    }

    private static func IsReplaceable(component: ref<IComponent>) -> Bool {
        if IsDefined(component as entGarmentSkinnedMeshComponent) {
            return false;
        }
        let category = RemoteLook.Category(NameToString(component.GetName()));
        return NotEquals(category, "t0") && NotEquals(category, "a0");
    }

    private static func SkinnedTo(component: ref<IComponent>) -> CName {
        let skin = component as entISkinTargetComponent;
        return IsDefined(skin) && IsDefined(skin.skinning) ? skin.skinning.bindName : n"None";
    }

    // An existing part to reuse: the part with that name (even if the reset kept it, like the body's seam mesh), else
    // a cleared part of the same kind and category skinned to the same thing (a reused part keeps its skeleton
    // binding). Reused parts get all mesh pieces switched on (chunkMask), since the old mesh's pieces don't fit the new
    // one. Null when there's none: the look then lacks that part.
    private static func TakePart(spare: script_ref<array<ref<IComponent>>>, components: array<ref<IComponent>>,
                                 kind: String, name: CName, skinnedTo: CName) -> ref<IComponent> {
        let i = 0;
        while i < ArraySize(Deref(spare)) {
            if Equals(Deref(spare)[i].GetName(), name) && Equals(RemoteLookApply.SkinnedTo(Deref(spare)[i]), skinnedTo) {
                let found = Deref(spare)[i];
                ArrayErase(Deref(spare), i);
                return found;
            }
            i += 1;
        }
        for component in components {
            if Equals(component.GetName(), name) && Equals(RemoteLookApply.SkinnedTo(component), skinnedTo) {
                return component;
            }
        }
        let category = RemoteLook.Category(NameToString(name));
        i = 0;
        while i < ArraySize(Deref(spare)) {
            let candidate = Deref(spare)[i];
            if RemoteLookApply.IsKind(candidate, kind) &&
               Equals(RemoteLook.Category(NameToString(candidate.GetName())), category) &&
               Equals(RemoteLookApply.SkinnedTo(candidate), skinnedTo) {
                ArrayErase(Deref(spare), i);
                RemoteLookApply.AllChunks(candidate);
                return candidate;
            }
            i += 1;
        }
        return null;
    }

    // Switches on every mesh piece: a new component's default.
    private static func AllChunks(component: ref<IComponent>) {
        let skinned = component as entSkinnedMeshComponent;
        if IsDefined(skinned) {
            skinned.chunkMask = new entSkinnedMeshComponent().chunkMask;
            return;
        }
        let morph = component as entMorphTargetSkinnedMeshComponent;
        if IsDefined(morph) {
            morph.chunkMask = new entMorphTargetSkinnedMeshComponent().chunkMask;
        }
    }

    private static func IsKind(component: ref<IComponent>, kind: String) -> Bool {
        let className = component.GetClassName();
        if Equals(kind, "M") {
            return Equals(className, n"entMorphTargetSkinnedMeshComponent");
        }
        if Equals(kind, "G") {
            return Equals(className, n"entGarmentSkinnedMeshComponent");
        }
        return Equals(className, n"entSkinnedMeshComponent");
    }
}
