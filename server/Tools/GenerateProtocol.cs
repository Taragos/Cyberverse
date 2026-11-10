// Generates both sides of the wire protocol from shared/protocol/protocol.yaml, the only place packets are defined:
//   shared/protocol/Protocol.h                        C++ structs for the client (serialized with zpp_bits)
//   server/Managed/Protocol/Generated/Protocol.g.cs   C# structs with their reader and writer
//
//   dotnet run server/Tools/GenerateProtocol.cs            write both files
//   dotnet run server/Tools/GenerateProtocol.cs -- --check  fail if either file is out of date (CI)
//
// Commit the generated files together with the schema.
#:package YamlDotNet@16.3.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

var check = args.Contains("--check");
if (args.Any(a => a != "--check"))
{
    Console.Error.WriteLine("Usage: dotnet run server/Tools/GenerateProtocol.cs [-- --check]");
    return 2;
}

var root = Gen.FindRepoRoot();
if (root == null)
{
    Console.Error.WriteLine("Could not find the repo root (shared/protocol/protocol.yaml) above the current directory");
    return 2;
}

var schemaPath = Path.Combine(root, "shared", "protocol", "protocol.yaml");
Schema schema;
try
{
    schema = Schema.Load(schemaPath);
}
catch (SchemaException e)
{
    Console.Error.WriteLine($"{schemaPath}: {e.Message}");
    return 1;
}

var outputs = new Dictionary<string, string>
{
    [Path.Combine(root, "shared", "protocol", "Protocol.h")] = CppEmitter.Emit(schema),
    [Path.Combine(root, "server", "Managed", "Protocol", "Generated", "Protocol.g.cs")] = CsEmitter.Emit(schema),
};

var stale = 0;
foreach (var (path, content) in outputs)
{
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    var current = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
    if (current == content)
    {
        Console.WriteLine($"Up to date: {relative}");
        continue;
    }

    if (check)
    {
        Console.Error.WriteLine($"Out of date: {relative}. Run: dotnet run server/Tools/GenerateProtocol.cs");
        stale++;
        continue;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    Console.WriteLine($"Wrote {relative}");
}

Console.WriteLine($"Protocol version 0x{schema.Version:X8}");
return stale == 0 ? 0 : 1;

class SchemaException(string message) : Exception(message);

enum Kind { Primitive, String, Enum, Struct }

record Primitive(string Cpp, string Cs, string Method, int Size);

record FieldType(Kind Kind, string Name, bool IsArray)
{
    public static readonly Dictionary<string, Primitive> Primitives = new()
    {
        ["u8"] = new("uint8_t", "byte", "U8", 1),
        ["u16"] = new("uint16_t", "ushort", "U16", 2),
        ["u32"] = new("uint32_t", "uint", "U32", 4),
        ["u64"] = new("uint64_t", "ulong", "U64", 8),
        ["i8"] = new("int8_t", "sbyte", "I8", 1),
        ["i16"] = new("int16_t", "short", "I16", 2),
        ["i32"] = new("int32_t", "int", "I32", 4),
        ["i64"] = new("int64_t", "long", "I64", 8),
        ["f32"] = new("float", "float", "F32", 4),
        ["f64"] = new("double", "double", "F64", 8),
        ["bool"] = new("bool", "bool", "Bool", 1),
    };

    public FieldType Element => this with { IsArray = false };

    public override string ToString() => IsArray ? Name + "[]" : Name;
}

record Field(string Name, FieldType Type, uint? Max, string? Doc)
{
    public string CsName => char.ToUpperInvariant(Name[0]) + Name[1..];
}

record EnumDef(string Name, string Type, List<string> Values, string? Doc);

record StructDef(string Name, List<Field> Fields, string? Doc);

record PacketDef(string Direction, string Name, ushort Id, byte Channel, bool Reliable, List<Field> Fields, string? Doc)
{
    public string EnumName => Direction == "serverbound" ? "EMessageTypeServerbound" : "EMessageTypeClientbound";
    public string Interface => Direction == "serverbound" ? "IServerBoundPacket" : "IClientBoundPacket";
}

class Schema
{
    public readonly List<EnumDef> Enums = [];
    public readonly List<StructDef> Structs = [];
    public readonly List<PacketDef> Packets = [];
    public uint Version;

    private readonly Dictionary<string, Kind> _types = new();
    private readonly HashSet<string> _packetNames = [];

    public IEnumerable<PacketDef> Direction(string direction) => Packets.Where(p => p.Direction == direction);

    public static Schema Load(string path)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path))
        {
            try
            {
                yaml.Load(reader);
            }
            catch (YamlDotNet.Core.YamlException e)
            {
                throw new SchemaException($"line {e.Start.Line}: {e.Message}");
            }
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode top)
        {
            throw new SchemaException("expected one YAML document with a mapping at the top");
        }

        var schema = new Schema();
        foreach (var (key, _) in top.Children)
        {
            var name = Scalar(key);
            if (name is not ("enums" or "structs" or "serverbound" or "clientbound"))
            {
                throw Error(key, $"unknown section '{name}' (expected enums, structs, serverbound, clientbound)");
            }
        }

        // Declare every type name first, so fields can refer to types in any order.
        var enums = Section(top, "enums");
        var structs = Section(top, "structs");
        foreach (var (key, _) in enums) schema.Declare(key, Kind.Enum);
        foreach (var (key, _) in structs) schema.Declare(key, Kind.Struct);

        foreach (var (key, value) in enums) schema.Enums.Add(schema.ParseEnum(Scalar(key), Mapping(value)));
        foreach (var (key, value) in structs)
        {
            var body = Mapping(value);
            CheckKeys(body, "fields", "doc");
            schema.Structs.Add(new StructDef(Scalar(key), schema.ParseFields(body), OptionalScalar(body, "doc")));
        }

        foreach (var direction in new[] { "serverbound", "clientbound" })
        {
            var ids = new Dictionary<ushort, string>();
            foreach (var (key, value) in Section(top, direction))
            {
                schema.Declare(key, null);
                var packet = schema.ParsePacket(direction, Scalar(key), Mapping(value));
                if (!ids.TryAdd(packet.Id, packet.Name))
                {
                    throw Error(key, $"{direction} id {packet.Id} is used by {ids[packet.Id]} and {packet.Name}");
                }
                schema.Packets.Add(packet);
            }
        }

        schema.CheckStructsAreNotRecursive();
        schema.Version = schema.ComputeVersion();
        return schema;
    }

    /// <summary>Registers a type name; a null kind declares a packet, which fields can't use as a type.</summary>
    private void Declare(YamlNode key, Kind? kind)
    {
        var name = Scalar(key);
        if (!IsIdentifier(name)) throw Error(key, $"'{name}' is not a valid identifier");
        if (FieldType.Primitives.ContainsKey(name) || name is "string" || name.StartsWith("EMessageType") ||
            name is "MessageFrame" or "Packet" or "PacketReader" or "PacketWriter" or "EnumCodec" or "ProtocolInfo")
        {
            throw Error(key, $"'{name}' is reserved");
        }
        if (_types.ContainsKey(name) || _packetNames.Contains(name)) throw Error(key, $"'{name}' is defined twice");
        if (kind is { } k) _types.Add(name, k);
        else _packetNames.Add(name);
    }

    private EnumDef ParseEnum(string name, YamlMappingNode body)
    {
        CheckKeys(body, "type", "values", "doc");
        var type = RequiredScalar(body, "type");
        if (type is not ("u8" or "u16" or "u32")) throw Error(body, $"enum {name}: type must be u8, u16 or u32");
        if (!body.Children.TryGetValue("values", out var valuesNode) || valuesNode is not YamlSequenceNode valueList)
        {
            throw Error(body, $"enum {name}: 'values' must be a list");
        }

        var values = valueList.Children.Select(Scalar).ToList();
        if (values.Count == 0) throw Error(body, $"enum {name} has no values");
        if (values.Distinct().Count() != values.Count) throw Error(body, $"enum {name} has a value twice");
        if (values.FirstOrDefault(v => !IsIdentifier(v)) is { } bad) throw Error(body, $"enum {name}: '{bad}' is not a valid identifier");
        var limit = type switch { "u8" => byte.MaxValue, "u16" => ushort.MaxValue, _ => uint.MaxValue };
        if ((ulong)values.Count - 1 > limit) throw Error(body, $"enum {name} has more values than {type} can hold");
        return new EnumDef(name, type, values, OptionalScalar(body, "doc"));
    }

    private PacketDef ParsePacket(string direction, string name, YamlMappingNode body)
    {
        CheckKeys(body, "id", "channel", "reliable", "fields", "doc");
        var id = ParseNumber(body, "id", ushort.MaxValue);
        var channel = ParseNumber(body, "channel", byte.MaxValue);
        var reliable = RequiredScalar(body, "reliable") switch
        {
            "true" => true,
            "false" => false,
            _ => throw Error(body, $"{name}: reliable must be true or false"),
        };
        return new PacketDef(direction, name, (ushort)id, (byte)channel, reliable, ParseFields(body), OptionalScalar(body, "doc"));
    }

    private List<Field> ParseFields(YamlMappingNode body)
    {
        if (!body.Children.TryGetValue("fields", out var node) || node is not YamlSequenceNode list)
        {
            throw Error(body, "'fields' must be a list (use [] for none)");
        }

        var fields = new List<Field>();
        foreach (var item in list.Children)
        {
            if (item is not YamlMappingNode { Children.Count: 1 } single)
            {
                throw Error(item, "a field is `name: type` or `name: { type: ..., max: ..., doc: ... }`");
            }

            var (keyNode, valueNode) = single.Children.First();
            var name = Scalar(keyNode);
            if (!IsIdentifier(name) || !char.IsLower(name[0])) throw Error(keyNode, $"field '{name}' must be a camelCase identifier");
            if (fields.Any(f => f.Name == name)) throw Error(keyNode, $"field '{name}' is defined twice");

            string typeName;
            uint? max = null;
            string? doc = null;
            if (valueNode is YamlMappingNode details)
            {
                CheckKeys(details, "type", "max", "doc");
                typeName = RequiredScalar(details, "type");
                if (details.Children.ContainsKey("max")) max = ParseNumber(details, "max", uint.MaxValue);
                doc = OptionalScalar(details, "doc");
            }
            else
            {
                typeName = Scalar(valueNode);
            }

            var type = ParseType(valueNode, typeName);
            if (max != null && !type.IsArray && type.Kind != Kind.String)
            {
                throw Error(valueNode, $"field '{name}': max only applies to strings and arrays");
            }
            fields.Add(new Field(name, type, max, doc));
        }
        return fields;
    }

    private FieldType ParseType(YamlNode node, string text)
    {
        var isArray = text.EndsWith("[]");
        var name = isArray ? text[..^2] : text;
        if (name.EndsWith("[]")) throw Error(node, $"'{text}': arrays of arrays aren't supported; wrap the inner array in a struct");
        if (FieldType.Primitives.ContainsKey(name)) return new FieldType(Kind.Primitive, name, isArray);
        if (name == "string") return new FieldType(Kind.String, name, isArray);
        if (_packetNames.Contains(name)) throw Error(node, $"'{name}' is a packet, not a field type");
        if (_types.TryGetValue(name, out var kind)) return new FieldType(kind, name, isArray);
        throw Error(node, $"unknown type '{name}'");
    }

    private void CheckStructsAreNotRecursive()
    {
        foreach (var start in Structs)
        {
            var stack = new Stack<(StructDef Def, string Path)>([(start, start.Name)]);
            while (stack.TryPop(out var item))
            {
                foreach (var field in item.Def.Fields.Where(f => f.Type.Kind == Kind.Struct))
                {
                    var path = $"{item.Path}.{field.Name}";
                    if (field.Type.Name == start.Name) throw new SchemaException($"struct {start.Name} contains itself ({path})");
                    stack.Push((Structs.First(s => s.Name == field.Type.Name), path));
                }
            }
        }

        // An array of zero-byte elements would let a tiny packet claim billions of them.
        foreach (var field in Structs.SelectMany(s => s.Fields).Concat(Packets.SelectMany(p => p.Fields)))
        {
            if (field.Type.IsArray && MinSize(field.Type.Element) == 0)
            {
                throw new SchemaException($"field '{field.Name}': arrays of empty structs aren't supported");
            }
        }
    }

    /// <summary>The fewest bytes a value of this type takes on the wire.</summary>
    public int MinSize(FieldType type) => type switch
    {
        { IsArray: true } => 4,
        { Kind: Kind.Primitive } => FieldType.Primitives[type.Name].Size,
        { Kind: Kind.String } => 4,
        { Kind: Kind.Enum } => FieldType.Primitives[Enums.First(e => e.Name == type.Name).Type].Size,
        _ => Structs.First(s => s.Name == type.Name).Fields.Sum(f => MinSize(f.Type)),
    };

    /// <summary>
    /// A hash of everything that affects the wire or the generated code (not comments or docs), so client and server
    /// built from different schemas see different versions at login.
    /// </summary>
    private uint ComputeVersion()
    {
        var text = new StringBuilder();
        foreach (var e in Enums) text.Append($"enum {e.Name}:{e.Type}={string.Join(',', e.Values)}\n");
        foreach (var s in Structs) text.Append($"struct {s.Name}{{{Describe(s.Fields)}}}\n");
        foreach (var p in Packets)
        {
            text.Append($"{p.Direction} {p.Name} id={p.Id} channel={p.Channel} reliable={p.Reliable}{{{Describe(p.Fields)}}}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);

        static string Describe(List<Field> fields) =>
            string.Join(';', fields.Select(f => $"{f.Name}:{f.Type}" + (f.Max is { } m ? $"<={m}" : "")));
    }

    private static IEnumerable<KeyValuePair<YamlNode, YamlNode>> Section(YamlMappingNode top, string name)
    {
        if (!top.Children.TryGetValue(name, out var node)) return [];
        if (node is YamlScalarNode { Value: null or "" }) return [];
        return node is YamlMappingNode map ? map.Children : throw Error(node, $"'{name}' must be a mapping");
    }

    private static void CheckKeys(YamlMappingNode node, params string[] allowed)
    {
        foreach (var (key, _) in node.Children)
        {
            if (!allowed.Contains(Scalar(key)))
            {
                throw Error(key, $"unknown key '{Scalar(key)}' (expected {string.Join(", ", allowed)})");
            }
        }
    }

    private static uint ParseNumber(YamlMappingNode node, string key, uint max)
    {
        var text = RequiredScalar(node, key);
        if (!uint.TryParse(text, out var value) || value > max) throw Error(node, $"'{key}' must be a number from 0 to {max}");
        return value;
    }

    private static YamlMappingNode Mapping(YamlNode node) =>
        node as YamlMappingNode ?? throw Error(node, "expected a mapping");

    private static string Scalar(YamlNode node) =>
        node is YamlScalarNode { Value: { } value } ? value : throw Error(node, "expected a plain value");

    private static string RequiredScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(key, out var value) ? Scalar(value) : throw Error(node, $"'{key}' is missing");

    private static string? OptionalScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(key, out var value) ? Scalar(value) : null;

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static SchemaException Error(YamlNode node, string message) => new($"line {node.Start.Line}: {message}");
}

static class CppEmitter
{
    public static string Emit(Schema schema)
    {
        var o = new StringBuilder();
        o.Append($$"""
            // <auto-generated>
            // Generated by server/Tools/GenerateProtocol.cs from shared/protocol/protocol.yaml. Don't edit it: change the
            // schema and run `dotnet run server/Tools/GenerateProtocol.cs`.
            // </auto-generated>
            #pragma once

            #include <cstdint>
            #include <string>
            #include <vector>

            /// A hash of protocol.yaml. The client sends it at login; the server rejects a different one.
            constexpr uint32_t PROTOCOL_VERSION_CURRENT = 0x{{schema.Version:X8}};

            /// The 4 bytes in front of every packet. zpp_bits writes the struct, then the packet's fields.
            struct MessageFrame
            {
                uint8_t channel_id{};
                uint16_t message_type{};
                uint8_t reserved{};
            };


            """);

        foreach (var direction in new[] { "serverbound", "clientbound" })
        {
            var packets = schema.Direction(direction).ToList();
            o.Append($"enum class {packets.FirstOrDefault()?.EnumName ?? EnumName(direction)} : uint16_t\n{{\n");
            foreach (var p in packets) o.Append($"    {p.Name} = {p.Id},\n");
            o.Append("};\n\n");
        }

        foreach (var e in schema.Enums)
        {
            Doc(o, e.Doc, "");
            o.Append($"enum class {e.Name} : {FieldType.Primitives[e.Type].Cpp}\n{{\n");
            foreach (var value in e.Values) o.Append($"    {value},\n");
            o.Append("};\n\n");
        }

        foreach (var s in schema.Structs)
        {
            Doc(o, s.Doc, "");
            o.Append($"struct {s.Name}\n{{\n");
            Fields(o, s.Fields);
            o.Append("};\n\n");
        }

        foreach (var direction in new[] { "serverbound", "clientbound" })
        {
            o.Append($"// {direction} packets\n\n");
            foreach (var p in schema.Direction(direction))
            {
                Doc(o, p.Doc, "");
                o.Append($"struct {p.Name}\n{{\n");
                Fields(o, p.Fields);
                if (p.Fields.Count > 0) o.Append('\n');
                o.Append($$"""
                        static constexpr {{p.EnumName}} MessageType = {{p.EnumName}}::{{p.Name}};
                        static constexpr uint8_t Channel = {{p.Channel}};
                        static constexpr bool Reliable = {{(p.Reliable ? "true" : "false")}};

                        static void FillMessageFrame(MessageFrame& frame)
                        {
                            frame.channel_id = Channel;
                            frame.message_type = static_cast<uint16_t>(MessageType);
                        }
                    };


                    """);
            }
        }

        return o.ToString().TrimEnd('\n') + "\n";
    }

    private static string EnumName(string direction) =>
        direction == "serverbound" ? "EMessageTypeServerbound" : "EMessageTypeClientbound";

    private static void Fields(StringBuilder o, List<Field> fields)
    {
        foreach (var f in fields)
        {
            var doc = FieldDoc(f);
            if (doc != null) o.Append($"    /// {doc}\n");
            var element = f.Type.Kind switch
            {
                Kind.Primitive => FieldType.Primitives[f.Type.Name].Cpp,
                Kind.String => "std::string",
                _ => f.Type.Name,
            };
            var type = f.Type.IsArray ? $"std::vector<{element}>" : element;
            o.Append($"    {type} {f.Name}{{}};\n");
        }
    }

    internal static string? FieldDoc(Field f)
    {
        var limit = f.Max is { } max ? f.Type.IsArray ? $"At most {max} elements." : $"At most {max} bytes." : null;
        var parts = new[] { f.Doc, limit }.Where(p => p != null).Select(p => p!.TrimEnd('.') + ".");
        var text = string.Join(" ", parts);
        return text.Length == 0 ? null : text;
    }

    private static void Doc(StringBuilder o, string? doc, string indent)
    {
        if (doc != null) o.Append($"{indent}/// {doc}\n");
    }
}

static class CsEmitter
{
    public static string Emit(Schema schema)
    {
        var o = new StringBuilder();
        o.Append($$"""
            // <auto-generated>
            // Generated by server/Tools/GenerateProtocol.cs from shared/protocol/protocol.yaml. Don't edit it: change the
            // schema and run `dotnet run server/Tools/GenerateProtocol.cs`.
            // </auto-generated>
            #nullable enable

            namespace Cyberverse.Server.Protocol;

            public static class ProtocolInfo
            {
                /// <summary>A hash of protocol.yaml. The client sends it at login; the server rejects a different one.</summary>
                public const uint Version = 0x{{schema.Version:X8}};
            }


            """);

        foreach (var direction in new[] { "serverbound", "clientbound" })
        {
            var name = direction == "serverbound" ? "EMessageTypeServerbound" : "EMessageTypeClientbound";
            o.Append($"public enum {name} : ushort\n{{\n");
            foreach (var p in schema.Direction(direction)) o.Append($"    {p.Name} = {p.Id},\n");
            o.Append("}\n\n");
        }

        foreach (var e in schema.Enums)
        {
            Doc(o, e.Doc, "");
            o.Append($"public enum {e.Name} : {FieldType.Primitives[e.Type].Cs}\n{{\n");
            foreach (var value in e.Values) o.Append($"    {value},\n");
            o.Append("}\n\n");
        }

        // Unknown values fail the packet, so handlers only ever see defined ones.
        o.Append("public static class EnumCodec\n{\n");
        foreach (var e in schema.Enums)
        {
            var p = FieldType.Primitives[e.Type];
            o.Append($$"""
                    public static bool TryRead(ref PacketReader reader, out {{e.Name}} value)
                    {
                        value = default;
                        if (!reader.TryRead{{p.Method}}(out var raw) || raw > {{e.Values.Count - 1}}) return false;
                        value = ({{e.Name}})raw;
                        return true;
                    }

                    public static void Write(PacketWriter writer, {{e.Name}} value) => writer.Write{{p.Method}}(({{p.Cs}})value);


                """);
        }
        TrimBlankLine(o);
        o.Append("}\n\n");

        foreach (var s in schema.Structs)
        {
            Type(o, schema, s.Name, s.Doc, s.Fields, $"IWireType<{s.Name}>", null);
        }

        foreach (var p in schema.Packets)
        {
            var members = $$"""
                    public static ushort MessageId => {{p.Id}};
                    public static byte Channel => {{p.Channel}};
                    public static bool Reliable => {{(p.Reliable ? "true" : "false")}};
                    public static {{p.EnumName}} MessageType => {{p.EnumName}}.{{p.Name}};

                """;
            Type(o, schema, p.Name, p.Doc, p.Fields, $"{p.Interface}<{p.Name}>", members);
        }

        return o.ToString().TrimEnd('\n') + "\n";
    }

    private static void Type(StringBuilder o, Schema schema, string name, string? doc, List<Field> fields, string iface,
        string? members)
    {
        Doc(o, doc, "");
        o.Append($"public partial struct {name} : {iface}\n{{\n");
        if (members != null) o.Append(members).Append('\n');

        foreach (var f in fields)
        {
            Doc(o, CppEmitter.FieldDoc(f), "    ");
            o.Append($"    public {CsType(f.Type)} {f.CsName};\n");
        }
        if (fields.Count > 0) o.Append('\n');

        o.Append("    public readonly void Write(PacketWriter writer)\n    {\n");
        foreach (var f in fields)
        {
            if (f.Type.IsArray)
            {
                o.Append($"        writer.WriteCount({f.CsName}?.Length ?? 0, {Max(f)});\n");
                o.Append($"        foreach (var item in {f.CsName} ?? []) {WriteStatement(f.Type.Element, "item", uint.MaxValue)}\n");
            }
            else
            {
                o.Append($"        {WriteStatement(f.Type, f.CsName, f.Max)}\n");
            }
        }
        o.Append("    }\n\n");

        o.Append($"    public static bool TryRead(ref PacketReader reader, out {name} value)\n    {{\n");
        o.Append("        value = default;\n");
        foreach (var f in fields)
        {
            var target = $"value.{f.CsName}";
            if (f.Type.IsArray)
            {
                var count = $"{f.Name}Count";
                o.Append($"        if (!reader.TryReadCount({Max(f)}, {schema.MinSize(f.Type.Element)}, out var {count})) return false;\n");
                o.Append($"        {target} = new {CsType(f.Type.Element)}[{count}];\n");
                o.Append($"        for (var i = 0; i < {count}; i++)\n        {{\n");
                o.Append($"            if (!{ReadExpression(f.Type.Element, $"{target}[i]", uint.MaxValue)}) return false;\n");
                o.Append("        }\n");
            }
            else
            {
                o.Append($"        if (!{ReadExpression(f.Type, target, f.Max)}) return false;\n");
            }
        }
        o.Append("        return true;\n    }\n}\n\n");
    }

    private static string Max(Field f) => f.Max is { } max ? max.ToString() : "uint.MaxValue";

    private static string CsType(FieldType type)
    {
        var element = type.Kind switch
        {
            Kind.Primitive => FieldType.Primitives[type.Name].Cs,
            Kind.String => "string",
            _ => type.Name,
        };
        return type.IsArray ? element + "[]" : element;
    }

    private static string WriteStatement(FieldType type, string value, uint? max) => type.Kind switch
    {
        Kind.Primitive => $"writer.Write{FieldType.Primitives[type.Name].Method}({value});",
        Kind.String => $"writer.WriteString({value}, {(max is { } m && m != uint.MaxValue ? m.ToString() : "uint.MaxValue")});",
        Kind.Enum => $"EnumCodec.Write(writer, {value});",
        _ => $"{value}.Write(writer);",
    };

    private static string ReadExpression(FieldType type, string target, uint? max) => type.Kind switch
    {
        Kind.Primitive => $"reader.TryRead{FieldType.Primitives[type.Name].Method}(out {target})",
        Kind.String => $"reader.TryReadString({(max is { } m && m != uint.MaxValue ? m.ToString() : "uint.MaxValue")}, out {target})",
        Kind.Enum => $"EnumCodec.TryRead(ref reader, out {target})",
        _ => $"{type.Name}.TryRead(ref reader, out {target})",
    };

    private static void Doc(StringBuilder o, string? doc, string indent)
    {
        if (doc == null) return;
        var escaped = doc.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        o.Append($"{indent}/// <summary>{escaped}</summary>\n");
    }

    private static void TrimBlankLine(StringBuilder o)
    {
        while (o.Length > 1 && o[^1] == '\n' && o[^2] == '\n') o.Length--;
    }
}

static class Gen
{
    public static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "shared", "protocol", "protocol.yaml"))) return dir.FullName;
        }
        return null;
    }
}
