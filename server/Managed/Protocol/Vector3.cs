namespace Cyberverse.Server.Protocol;

// The fields and wire code are generated (Generated/Protocol.g.cs); this adds the helpers the server uses.
public partial struct Vector3
{
    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // TODO: would we rather want to cast into System.Numerics? At least for more complicated operations we should!
    public readonly float DistanceSquared(Vector3 b)
    {
        return (X - b.X) * (X - b.X) + (Y - b.Y) * (Y - b.Y) + (Z - b.Z) * (Z - b.Z);
    }

    public override readonly string ToString()
    {
        return $"({X}, {Y}, {Z})";
    }
}
