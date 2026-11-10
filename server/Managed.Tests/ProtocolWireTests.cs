using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Cyberverse.Server.Protocol;

namespace Cyberverse.Server.Tests;

/// <summary>
/// The client serializes packets with zpp_bits from shared/protocol/Protocol.h, the server with the generated C# writer.
/// This fills every packet with the same values on both sides and checks the bytes match, frame header included, and
/// that C# reads the client's bytes back. clang++ has to be on the PATH. The packets with padding in their C++ layout
/// (PlayerEquipItem, EquipItemEntity, PlayerActionTracked, PlayerSpawnCar) and the string in InitAuth are the cases
/// that would break if zpp_bits didn't write field by field.
/// </summary>
public class ProtocolWireTests
{
    public static TheoryData<Type> Packets => new(PacketTypes);

    private static IEnumerable<Type> PacketTypes => typeof(ProtocolInfo).Assembly.GetTypes()
        .Where(t => t.IsValueType && t.GetInterfaces().Any(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IServerBoundPacket<>) ||
             i.GetGenericTypeDefinition() == typeof(IClientBoundPacket<>))))
        .OrderBy(t => t.Name);

    [Fact]
    public void EveryPacketIsCovered()
    {
        // 8 serverbound and 5 clientbound packets when this was written; the list itself comes from reflection.
        Assert.Equal(13, PacketTypes.Count());
    }

    [Theory]
    [MemberData(nameof(Packets))]
    public void CSharpWritesTheSameBytesAsZppBits(Type type)
    {
        var (sample, _) = Samples.Value[type.Name];
        Assert.True(CppBytes.Value.TryGetValue(type.Name, out var cpp), $"The C++ program printed nothing for {type.Name}");
        Assert.Equal(Convert.ToHexString(cpp), Convert.ToHexString(WriteMessage(sample)));
    }

    [Theory]
    [MemberData(nameof(Packets))]
    public void CSharpReadsWhatZppBitsWrote(Type type)
    {
        var cpp = CppBytes.Value[type.Name];
        var read = typeof(ProtocolWireTests).GetMethod(nameof(ReadMessage), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type);
        var packet = read.Invoke(null, [cpp]);
        Assert.NotNull(packet);
        // Writing it again gives the same bytes only if every field was read back with its value.
        Assert.Equal(Convert.ToHexString(cpp), Convert.ToHexString(WriteMessage(packet)));
    }

    private static object? ReadMessage<T>(byte[] message) where T : struct, IPacket<T>
    {
        var reader = new PacketReader(message);
        Assert.True(MessageFrame.TryRead(ref reader, out var frame));
        Assert.Equal(new MessageFrame(T.Channel, T.MessageId), frame);
        return Packet.TryRead<T>(message.AsSpan(MessageFrame.Size), out var packet) ? packet : null;
    }

    private static byte[] WriteMessage(object packet)
    {
        var writer = new PacketWriter();
        typeof(Packet).GetMethod(nameof(Packet.Write))!.MakeGenericMethod(packet.GetType()).Invoke(null, [writer, packet]);
        return writer.ToArray();
    }

    /// <summary>Per packet type: a C# value with every field set, and the same value as a C++ expression.</summary>
    private static readonly Lazy<Dictionary<string, (object Value, string Cpp)>> Samples = new(() =>
    {
        var builder = new SampleBuilder();
        return PacketTypes.ToDictionary(t => t.Name, t => builder.Build(t));
    });

    private static readonly Lazy<Dictionary<string, byte[]>> CppBytes = new(RunCpp);

    private static Dictionary<string, byte[]> RunCpp()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "shared", "protocol", "Protocol.h")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("Repository root with shared/protocol not found");
        }

        var source = new StringBuilder("""
            #include <Protocol.h>
            #include <zpp_bits.h>
            #include <bit>
            #include <cstdio>

            // What the client's NetworkGameSystem::EnqueueMessage does: frame, then packet.
            template <typename T> void dump(const char* name, const T& packet)
            {
                auto [data, out] = zpp::bits::data_out();
                MessageFrame frame{};
                T::FillMessageFrame(frame);
                if (zpp::bits::failure(out(frame)) || zpp::bits::failure(out(packet)))
                {
                    std::printf("%s FAILED\n", name);
                    return;
                }
                std::printf("%s ", name);
                for (const auto b : data) std::printf("%02x", static_cast<unsigned>(b));
                std::printf("\n");
            }

            int main()
            {

            """);
        foreach (var (name, (_, cpp)) in Samples.Value)
        {
            source.AppendLine($"    dump(\"{name}\", {cpp});");
        }
        source.AppendLine("}");

        var dir = Directory.CreateTempSubdirectory("cyberverse-wire-");
        try
        {
            var file = Path.Combine(dir.FullName, "wire.cpp");
            var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "wire.exe" : "wire");
            File.WriteAllText(file, source.ToString());
            // The client's copy of zpp_bits, since the client is the side that writes with it.
            var zppBits = Path.Combine(root.FullName, "client", "red4ext", "deps", "zpp_bits");
            Run("clang++", ["-std=c++20", "-w", "-I", Path.Combine(root.FullName, "shared", "protocol"), "-I", zppBits,
                file, "-o", exe]);
            var output = Run(exe, []);

            var result = new Dictionary<string, byte[]>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ');
                Assert.True(parts[1] != "FAILED", $"zpp_bits failed to serialize {parts[0]}");
                result[parts[0]] = Convert.FromHexString(parts[1]);
            }
            return result;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string Run(string program, string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"Could not start {program} (clang++ has to be on the PATH)", e);
        }

        using var _ = process;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} failed ({process.ExitCode}):\n{stderr.Result}");
        }
        return stdout.Result;
    }

    /// <summary>
    /// Fills fields with distinct values that use every byte (high bits set, non-ASCII strings), so a swapped, shifted or
    /// truncated field changes the bytes.
    /// </summary>
    private sealed class SampleBuilder
    {
        private ulong _counter;

        private ulong Next() => unchecked(++_counter * 0x9E3779B97F4A7C15UL);

        public (object Value, string Cpp) Build(Type type)
        {
            if (type == typeof(bool))
            {
                var value = (Next() & 1) == 1;
                return (value, value ? "true" : "false");
            }
            if (type == typeof(byte)) { var v = (byte)Next(); return (v, $"uint8_t{{{v}}}"); }
            if (type == typeof(sbyte)) { var v = (sbyte)Next(); return (v, $"int8_t{{{v}}}"); }
            if (type == typeof(ushort)) { var v = (ushort)Next(); return (v, $"uint16_t{{{v}}}"); }
            if (type == typeof(short)) { var v = (short)Next(); return (v, $"int16_t{{{v}}}"); }
            if (type == typeof(uint)) { var v = (uint)Next(); return (v, $"uint32_t{{{v}u}}"); }
            if (type == typeof(int)) { var v = (int)Next(); return (v, $"int32_t{{static_cast<int32_t>({(uint)v}u)}}"); }
            if (type == typeof(ulong)) { var v = Next(); return (v, $"uint64_t{{{v}ull}}"); }
            if (type == typeof(long)) { var v = (long)Next(); return (v, $"int64_t{{static_cast<int64_t>({(ulong)v}ull)}}"); }
            if (type == typeof(float))
            {
                // A finite float with a fractional part, passed as its bits so both sides have it exactly.
                var v = (float)(Next() % 100000) / 7f;
                return (v, $"std::bit_cast<float>({BitConverter.SingleToUInt32Bits(v)}u)");
            }
            if (type == typeof(double))
            {
                var v = (double)(Next() % 100000) / 7d;
                return (v, $"std::bit_cast<double>({BitConverter.DoubleToUInt64Bits(v)}ull)");
            }
            if (type == typeof(string))
            {
                var v = $"Jürgen-{_counter}-€";
                var escaped = string.Concat(Encoding.UTF8.GetBytes(v).Select(b => $"\\x{b:x2}"));
                return (v, $"std::string(\"{escaped}\", {Encoding.UTF8.GetByteCount(v)})");
            }
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                var v = values.GetValue((int)(Next() % (ulong)values.Length))!;
                return (v, $"static_cast<{type.Name}>({Convert.ToUInt64(v, CultureInfo.InvariantCulture)})");
            }
            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                var array = Array.CreateInstance(element, 2);
                var items = new List<string>();
                for (var i = 0; i < array.Length; i++)
                {
                    var (value, cpp) = Build(element);
                    array.SetValue(value, i);
                    items.Add(cpp);
                }
                return (array, $"std::vector<{CppName(element)}>{{{string.Join(", ", items)}}}");
            }

            // A generated struct: fields in declaration order, which is the wire order.
            var instance = Activator.CreateInstance(type)!;
            var fields = new List<string>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.MetadataToken))
            {
                var (value, cpp) = Build(field.FieldType);
                field.SetValue(instance, value);
                fields.Add(cpp);
            }
            return (instance, $"{type.Name}{{{string.Join(", ", fields)}}}");
        }

        private static string CppName(Type type) => type switch
        {
            _ when type == typeof(string) => "std::string",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(byte) => "uint8_t",
            _ when type == typeof(sbyte) => "int8_t",
            _ when type == typeof(ushort) => "uint16_t",
            _ when type == typeof(short) => "int16_t",
            _ when type == typeof(uint) => "uint32_t",
            _ when type == typeof(int) => "int32_t",
            _ when type == typeof(ulong) => "uint64_t",
            _ when type == typeof(long) => "int64_t",
            _ when type == typeof(float) => "float",
            _ when type == typeof(double) => "double",
            _ => type.Name,
        };
    }
}
