module Cyberverse.Network.Managers
import Codeware.*
import Cyberverse.Characters.*

public native class NetworkGameSystem extends IGameSystem {
    //public native func ConnectToServer(host: String, port: Uint16) -> Void;
    native let FullyConnected: Bool;
    native let playerActionTracker: ref<PlayerActionTracker>;
    public native func EnqueueLoadLastCheckpoint(handler: wref<inkISystemRequestsHandler>) -> Void;
    public native func TakeLoginRejectedMessage() -> String;
    // The browser login link and code while the server waits for it, once; else "". Not shown on screen yet (CYB-00023).
    public native func TakeLoginPrompt() -> String;
    public native func Log(message: String) -> Void;

    // Characters (CYB-00009). The last CharacterList, last played first; out of range gives "".
    public native func GetCharacterCount() -> Int32;
    public native func GetCharacterId(index: Int32) -> String;
    public native func GetCharacterName(index: Int32) -> String;
    public native func GetMaxCharacters() -> Int32;
    // Requests to the server; false when not connected. Answered through the callbacks below.
    public native func CreateCharacter(firstName: String, lastName: String) -> Bool;
    public native func SelectCharacter(characterId: String) -> Bool;
    public native func DeleteCharacter(characterId: String) -> Bool;
    // Stores the look of the character being played (CharacterAppearanceEditor's encoding).
    public native func SendCharacterAppearance(appearance: String) -> Bool;
    // Other players' looks (CYB-00029, see RemoteLook.reds): the changes for a stored look, and everything the look
    // table can change on a body ("m" or "f").
    public native func ResolveLook(look: String) -> ref<LookChanges>;
    public native func LookResetList(body: String) -> ref<LookChanges>;
    // Points a mesh part the engine has set up at another file by path; false if Codeware refused.
    public native func ChangePartResource(component: ref<IScriptable>, path: String) -> Bool;
    // The look's hair as the game's own parts (extracted from its .app files), renamed with "cyberverse_". Only works
    // when added while the engine builds the entity (Codeware's Entity/Initialize).
    public native func HairParts(look: String) -> array<ref<IScriptable>>;
    // The last look received for a spawned entity; empty if none.
    public native func LookOf(entity: EntityID) -> String;

    // Called by the plugin when the server's character packets arrive.
    public func OnCharacterList() {
        CharacterSelection.Get().OnList();
    }

    // A remote player character's look arrived (CYB-00029).
    public func OnEntityLook(entityId: EntityID, appearance: String) {
        RemoteLook.Apply(entityId, appearance);
    }

    // Every 0.25 s while connected: steps remote looks waiting for their puppet (CYB-00029).
    public func TickRemoteLooks() {
        RemoteLookQueue.Get().Tick();
    }

    public func OnCharacterRequestFailed(message: String) {
        CharacterSelection.Get().OnFailed(message);
    }

    public func OnCharacterSpawned(name: String, teleport: Bool, position: Vector4, yaw: Float, appearance: String) {
        CharacterSelection.Get().OnSpawned(name, teleport, position, yaw, appearance);
    }
    
    public func SpawnTransientEntity(entityName: TweakDBID, worldPosition: Vector4, worldOrientation: Quaternion) -> EntityID {
        let npcSpec = new DynamicEntitySpec();
        //npcSpec.recordID = t"Character.spr_animals_bouncer1_ranged1_omaha_mb";
        npcSpec.recordID = entityName; //t"Character.Panam";
        //npcSpec.appearanceName = n"random"; // TODO

        // Trust the server to properly track the entity state, because otherwise,
        // entities will just disappear for the client and never get back
        // (i.e. being invisible), as the server will only re-spawn them when
        // they have been properly despawned.
        npcSpec.alwaysSpawned = true;
        
        // base\characters\entities\main_npc\panam.ent
        //npcSpec.recordID = t"Vehicle.v_sport2_quadra_type66";
        //npcSpec.appearanceName = n"quadra_type66__basic_bulleat";

        npcSpec.position = worldPosition;
        npcSpec.orientation = worldOrientation;
        npcSpec.persistState = false;
        npcSpec.persistSpawn = false;
        npcSpec.tags = [n"RED4ext"];

        return GameInstance.GetDynamicEntitySystem().CreateEntity(npcSpec);
    }

    public func DestroyTransientEntity(entityId: EntityID) {
        GameInstance.GetDynamicEntitySystem().DeleteEntity(entityId);
    }

    public func TeleportEntity(game: GameInstance, entity: ref<Entity>, position: Vector4, worldOrientation: EulerAngles) {
        // TODO: there is no SetWorldPosition.
        //let entity = GameInstance.GetDynamicEntitySystem().GetEntity(id);
        //let transform = .GetWorldTransform();
        // let worldPosition = WorldTransform.GetWorldPosition(transform);
        // WorldPosition.SetVector4(worldPosition, position);
        // WorldTransform.SetPosition(transform, position);

        // We need GameObjects, not pure Entites.
        //GameInstance.GetTeleportationFacility(game).Teleport(entity as GameObject, position, worldOrientation);
    }

    public func TeleportPuppet(entity: ref<Entity>, position: Vector4, rotation: Float) -> ref<AICommand> {
        let puppet = entity as ScriptedPuppet;
        if !IsDefined(puppet) || !IsDefined(puppet.GetAIControllerComponent()) {
            return null; // cars, items, or a puppet without AI: the teleportation facility moves those
        }
        let teleportCommand = new AITeleportCommand();
        teleportCommand.position = position;
        teleportCommand.rotation = rotation;
        teleportCommand.doNavTest = false;

        puppet.GetAIControllerComponent().SendCommand(teleportCommand);
        puppet.GetAIControllerComponent().DisableCollider(); // TODO: Temp - In the future this should be controlled by the server, but currently Judy's just annoying :D 
        puppet.GetAIControllerComponent().ForceTickNextFrame();

        // let attackCommand = new AIMeleeAttackCommand();
        // puppet.GetAIControllerComponent().SendCommand(attackCommand);

        // let weapon = ScriptedPuppet.GetActiveWeapon(puppet);
        // weapon.ShootStraight(true);

        return teleportCommand;
    }

    // Remote puppets' walk animation (CYB-00010): the plugin builds the native AnimFeature_Locomotion each frame
    // (redscript can't create it) and hands it here. Which input name the NPC graph reads isn't known yet, so it goes
    // out under the candidates at once; only NPC puppets get it, not cars or items.
    public func ApplyLocomotion(entity: ref<Entity>, feature: ref<AnimFeature>) -> Void {
        let puppet = entity as NPCPuppet;
        if !IsDefined(puppet) {
            return;
        }
        AnimationControllerComponent.ApplyFeature(puppet, n"Locomotion", feature);
        AnimationControllerComponent.ApplyFeature(puppet, n"locomotion", feature);
        AnimationControllerComponent.ApplyFeature(puppet, n"LocomotionFeature", feature);
        AnimationControllerComponent.ApplyFeature(puppet, n"Movement", feature);
    }

    // The length of one of the puppet's animations in seconds (e.g. walk_0); 0 or less when unknown.
    public func AnimationLength(entity: ref<Entity>, animation: CName) -> Float {
        let puppet = entity as ScriptedPuppet;
        if !IsDefined(puppet) {
            return -1.0;
        }
        let animationController = puppet.GetAnimationControllerComponent();
        if !IsDefined(animationController) {
            return -1.0;
        }
        return animationController.GetAnimationDuration(animation);
    }

    public func StopAICommand(puppet: ref<ScriptedPuppet>, command: ref<AICommand>) {
        let component = puppet.GetAIControllerComponent();
        if (EnumInt(component.GetCommandState(command)) != EnumInt(AICommandState.Success)) {
            component.CancelCommand(command);
        }
    }
}

@addMethod(GameInstance)
public static native func GetNetworkGameSystem() -> ref<NetworkGameSystem>
