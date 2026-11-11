using Cyberverse.Server.Types;

namespace Cyberverse.Server.TweakDb;

/// <summary>
/// Records the Cyberverse client adds to the game's TweakDB, which the generated enums can't know. Keep the names in
/// step with <c>client/tweaks/remote_v.yaml</c>.
/// </summary>
public static class CyberverseRecords
{
    /// <summary>Other players' male V (CYB-00032): the cutscene V with our own entity template.</summary>
    public static readonly TweakDbId RemoteVMale = RecordIdUtils.ToRecordId("Character.Cyberverse_Remote_V_Male");

    /// <summary>Other players' female V (CYB-00032).</summary>
    public static readonly TweakDbId RemoteVFemale = RecordIdUtils.ToRecordId("Character.Cyberverse_Remote_V_Female");
}
