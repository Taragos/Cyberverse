using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Types;

/// <summary>
/// Don't overuse this class, it's just a simple dataclass, we want to handle complex data in a different way.
///
/// Technically the only thing an entity is is it's entityId, both the recordId (which is the game asset for it and
/// the position are contextually relevant only and could be different maps based on the entityId).
/// </summary>
public class Entity
{
    public readonly ulong NetworkedEntityId;
    /// <summary>
    /// 0 is unowned, could be server controlled.
    /// </summary>
    public ulong NetworkIdOwner;
    public ulong RecordId;
    public Vector3 WorldTransform;
    public float Yaw;
    // TODO: Remove and derive from recordId
    public bool IsVehicle;
    /// <summary>
    /// A player character's stored look ("v1 body=m name=value ..."), sent to everyone who sees it (EntityLook,
    /// CYB-00029); null for everything else.
    /// </summary>
    public string? Look;
    /// <summary>
    /// A player's character: moved by their PlayerPositionUpdate and sent to others as EntityMovement (CYB-00010).
    /// </summary>
    public bool IsCharacter;
    /// <summary>A character's horizontal speed in m/s, from the last PlayerPositionUpdate.</summary>
    public float Speed;
    public EMovementState MovementState;
    /// <summary>The owner's clock in milliseconds at the last PlayerPositionUpdate.</summary>
    public uint MovementTime;
    /// <summary>Counts the character's movement updates, for the EntityMovement sequence; 0 before the first.</summary>
    public uint MovementSequence;

    public Entity(ulong entityId, ulong recordId)
    {
        NetworkedEntityId = entityId;
        RecordId = recordId;
    }
}