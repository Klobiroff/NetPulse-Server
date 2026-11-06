using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace NetPulse.Unity.Generators
{
    internal enum SerKind
    {
        Primitive,
        Enum,
        Unity,
        Reference,
        Custom,
        Array,
        List
    }

    internal sealed class TypeSer
    {
        private const string WriterExtensions = "global::NetPulse.NetPulseWriterExtensions";
        private const string ReaderExtensions = "global::NetPulse.NetPulseReaderExtensions";

        private static readonly HashSet<string> UnityTypes = new HashSet<string>
        {
            "Vector2",
            "Vector3",
            "Vector4",
            "Quaternion",
            "Color",
            "Color32",
            "Vector2Int",
            "Vector3Int",
            "Rect"
        };

        private TypeSer(SerKind kind, ITypeSymbol type)
        {
            Kind = kind;
            Type = type;
            TypeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            MinSize = 1;
        }

        public SerKind Kind { get; }

        public ITypeSymbol Type { get; }

        public string TypeName { get; }

        public string Suffix { get; private set; }

        public string UnderlyingName { get; private set; }

        public TypeSer Element { get; private set; }

        public int MinSize { get; private set; }

        public static string SupportedList =>
            "bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, char, string, System.Guid, enums, "
            + "Vector2, Vector3, Vector4, Quaternion, Color, Color32, Vector2Int, Vector3Int, Rect, Player, NetworkObject, "
            + "arrays and List<T> of these, and types marked [NetSerializable] or implementing INetMessage";

        public static bool TryCreate(ITypeSymbol type, KnownSymbols known, out TypeSer ser) => TryCreate(type, known, 0, out ser);

        private static bool TryCreate(ITypeSymbol type, KnownSymbols known, int depth, out TypeSer ser)
        {
            ser = null;

            if (type == null || type.TypeKind == TypeKind.Error || depth > 8)
            {
                return false;
            }

            if (TryPrimitive(type.SpecialType, out var suffix, out var size))
            {
                ser = new TypeSer(SerKind.Primitive, type) { Suffix = suffix, MinSize = size };
                return true;
            }

            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType && enumType.EnumUnderlyingType != null && TryPrimitive(enumType.EnumUnderlyingType.SpecialType, out var enumSuffix, out var enumSize))
            {
                ser = new TypeSer(SerKind.Enum, type)
                {
                    Suffix = enumSuffix,
                    MinSize = enumSize,
                    UnderlyingName = enumType.EnumUnderlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                };
                return true;
            }

            if (type is IArrayTypeSymbol array)
            {
                if (array.Rank != 1 || !TryCreate(array.ElementType, known, depth + 1, out var arrayElement))
                {
                    return false;
                }

                ser = new TypeSer(SerKind.Array, type) { Element = arrayElement };
                return true;
            }

            if (!(type is INamedTypeSymbol named))
            {
                return false;
            }

            if (named.IsGenericType && known.ListDefinition != null && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, known.ListDefinition))
            {
                if (!TryCreate(named.TypeArguments[0], known, depth + 1, out var listElement))
                {
                    return false;
                }

                ser = new TypeSer(SerKind.List, type) { Element = listElement };
                return true;
            }

            if (known.Guid != null && SymbolEqualityComparer.Default.Equals(named, known.Guid))
            {
                ser = new TypeSer(SerKind.Primitive, type) { Suffix = "Guid", MinSize = 16 };
                return true;
            }

            if (named.ContainingNamespace != null && named.ContainingNamespace.ToDisplayString() == "UnityEngine" && named.TypeKind == TypeKind.Struct && UnityTypes.Contains(named.Name))
            {
                ser = new TypeSer(SerKind.Unity, type) { Suffix = named.Name };
                return true;
            }

            if (known.Player != null && SymbolEqualityComparer.Default.Equals(named, known.Player))
            {
                ser = new TypeSer(SerKind.Reference, type) { Suffix = "Player" };
                return true;
            }

            if (known.NetworkObject != null && SymbolEqualityComparer.Default.Equals(named, known.NetworkObject))
            {
                ser = new TypeSer(SerKind.Reference, type) { Suffix = "NetworkObject" };
                return true;
            }

            if ((named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct) && !named.IsGenericType && known.IsSerializableType(named))
            {
                ser = new TypeSer(SerKind.Custom, type) { MinSize = 0 };
                return true;
            }

            return false;
        }

        private static bool TryPrimitive(SpecialType special, out string suffix, out int size)
        {
            size = 1;

            switch (special)
            {
                case SpecialType.System_Boolean:
                    suffix = "Bool";
                    return true;
                case SpecialType.System_Byte:
                    suffix = "Byte";
                    return true;
                case SpecialType.System_SByte:
                    suffix = "SByte";
                    return true;
                case SpecialType.System_Int16:
                    suffix = "Short";
                    size = 2;
                    return true;
                case SpecialType.System_UInt16:
                    suffix = "UShort";
                    size = 2;
                    return true;
                case SpecialType.System_Int32:
                    suffix = "Int";
                    size = 4;
                    return true;
                case SpecialType.System_UInt32:
                    suffix = "UInt";
                    size = 4;
                    return true;
                case SpecialType.System_Int64:
                    suffix = "Long";
                    size = 8;
                    return true;
                case SpecialType.System_UInt64:
                    suffix = "ULong";
                    size = 8;
                    return true;
                case SpecialType.System_Single:
                    suffix = "Float";
                    size = 4;
                    return true;
                case SpecialType.System_Double:
                    suffix = "Double";
                    size = 8;
                    return true;
                case SpecialType.System_Char:
                    suffix = "Char";
                    size = 2;
                    return true;
                case SpecialType.System_String:
                    suffix = "String";
                    return true;
                default:
                    suffix = null;
                    return false;
            }
        }

        public string WriteExpression(string writer, string value)
        {
            switch (Kind)
            {
                case SerKind.Primitive:
                    return writer + ".Write" + Suffix + "(" + value + ")";
                case SerKind.Enum:
                    return writer + ".Write" + Suffix + "((" + UnderlyingName + ")" + value + ")";
                case SerKind.Unity:
                case SerKind.Reference:
                    return WriterExtensions + ".Write" + Suffix + "(" + writer + ", " + value + ")";
                case SerKind.Custom:
                    return TypeName + ".NetWrite(" + writer + ", " + value + ")";
                default:
                    return null;
            }
        }

        public string ReadExpression(string reader)
        {
            switch (Kind)
            {
                case SerKind.Primitive:
                    return reader + ".Read" + Suffix + "()";
                case SerKind.Enum:
                    return "(" + TypeName + ")" + reader + ".Read" + Suffix + "()";
                case SerKind.Unity:
                case SerKind.Reference:
                    return ReaderExtensions + ".Read" + Suffix + "(" + reader + ")";
                case SerKind.Custom:
                    return TypeName + ".NetRead(" + reader + ")";
                default:
                    return null;
            }
        }

        public void EmitWrite(CodeWriter code, string writer, string value)
        {
            var expression = WriteExpression(writer, value);

            if (expression != null)
            {
                code.Line(expression + ";");
                return;
            }

            var id = code.Next();
            var collection = "__c" + id;
            var count = "__n" + id;
            var index = "__i" + id;
            var length = Kind == SerKind.Array ? "Length" : "Count";

            code.Open("");
            code.Line("var " + collection + " = " + value + ";");
            code.Line("var " + count + " = " + collection + " == null ? 0 : " + collection + "." + length + ";");
            code.Line(writer + ".WriteVarUInt((uint)" + count + ");");
            code.Open("for (var " + index + " = 0; " + index + " < " + count + "; " + index + "++)");
            Element.EmitWrite(code, writer, collection + "[" + index + "]");
            code.Close();
            code.Close();
        }

        public string EmitRead(CodeWriter code, string reader)
        {
            var expression = ReadExpression(reader);

            if (expression != null)
            {
                return expression;
            }

            var id = code.Next();
            var count = "__n" + id;
            var index = "__i" + id;
            var result = "__r" + id;
            code.Line("var " + count + " = " + reader + ".ReadCount(" + Element.MinSize + ");");

            if (Kind == SerKind.Array)
            {
                code.Line("var " + result + " = " + CreateArray(count) + ";");
            }
            else
            {
                code.Line("var " + result + " = new " + TypeName + "(" + count + ");");
            }

            code.Open("for (var " + index + " = 0; " + index + " < " + count + "; " + index + "++)");
            var element = Element.EmitRead(code, reader);
            code.Line(Kind == SerKind.Array ? result + "[" + index + "] = " + element + ";" : result + ".Add(" + element + ");");
            code.Close();
            return result;
        }

        private string CreateArray(string count)
        {
            var element = ((IArrayTypeSymbol)Type).ElementType;
            var suffix = "";

            while (element is IArrayTypeSymbol inner)
            {
                suffix += "[]";
                element = inner.ElementType;
            }

            return "new " + element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "[" + count + "]" + suffix;
        }
    }
}
