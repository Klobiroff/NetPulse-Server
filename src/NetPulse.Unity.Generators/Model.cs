using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetPulse.Unity.Generators
{
    internal sealed class RpcParam
    {
        public RpcParam(string name, TypeSer ser)
        {
            Name = name;
            Ser = ser;
        }

        public string Name { get; }

        public TypeSer Ser { get; }
    }

    internal sealed class RpcModel
    {
        public IMethodSymbol Method { get; set; }

        public string Name => Method.Name;

        public ushort Id { get; set; }

        public List<RpcParam> Params { get; } = new List<RpcParam>();

        public bool AcceptsInfo { get; set; }

        public string TargetExpression { get; set; }

        public string DeliveryExpression { get; set; }

        public bool OwnerOnly { get; set; }
    }

    internal sealed class RpcIssue
    {
        public RpcIssue(IParameterSymbol parameter, string hint)
        {
            Parameter = parameter;
            Hint = hint;
        }

        public IParameterSymbol Parameter { get; }

        public string Hint { get; }
    }

    internal sealed class RpcCheck
    {
        public RpcModel Model { get; set; }

        public bool BadSignature { get; set; }

        public List<RpcIssue> BadParams { get; } = new List<RpcIssue>();

        public bool IsValid => Model != null;
    }

    internal sealed class RpcSet
    {
        public List<RpcModel> Own { get; } = new List<RpcModel>();

        public List<RpcModel> Inherited { get; } = new List<RpcModel>();

        public List<KeyValuePair<RpcModel, RpcModel>> Collisions { get; } = new List<KeyValuePair<RpcModel, RpcModel>>();

        public bool BaseHasRpc { get; set; }
    }

    internal sealed class SyncVarModel
    {
        public SyncVarModel(IFieldSymbol field, TypeSer ser)
        {
            Field = field;
            Ser = ser;
        }

        public IFieldSymbol Field { get; }

        public TypeSer Ser { get; }
    }

    internal sealed class SerMember
    {
        public SerMember(ISymbol symbol, ITypeSymbol type, TypeSer ser)
        {
            Symbol = symbol;
            Type = type;
            Ser = ser;
        }

        public ISymbol Symbol { get; }

        public ITypeSymbol Type { get; }

        public TypeSer Ser { get; }

        public string Name => Symbol.Name;
    }

    internal static class Discovery
    {
        public const string DefaultTarget = "global::NetPulse.RpcTarget.All";
        public const string DefaultDelivery = "global::NetPulse.Common.DeliveryMode.ReliableOrdered";

        public static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        public static bool IsRpc(IMethodSymbol method, KnownSymbols known) =>
            method.MethodKind == MethodKind.Ordinary && known.HasAttribute(method, known.NetRpcAttribute);

        public static RpcCheck CheckRpc(IMethodSymbol method, KnownSymbols known)
        {
            var check = new RpcCheck();

            if (!method.ReturnsVoid)
            {
                check.BadSignature = true;
            }

            var model = new RpcModel { Method = method, Id = StableId.Compute(method.Name) };

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];

                if (parameter.RefKind != RefKind.None)
                {
                    check.BadSignature = true;
                    continue;
                }

                var isLast = i == method.Parameters.Length - 1;
                var isInfo = known.NetRpcInfo != null && SymbolEqualityComparer.Default.Equals(parameter.Type, known.NetRpcInfo);

                if (isInfo && isLast)
                {
                    model.AcceptsInfo = true;
                    continue;
                }

                if (isInfo)
                {
                    check.BadParams.Add(new RpcIssue(parameter, "NetRpcInfo is only allowed as the last parameter. Move it to the end of the parameter list."));
                    continue;
                }

                if (!TypeSer.TryCreate(parameter.Type, known, out var ser))
                {
                    check.BadParams.Add(new RpcIssue(parameter, "Supported types: " + TypeSer.SupportedList + ". Mark a custom type with [NetSerializable] (it must be partial) or pass its fields as separate parameters."));
                    continue;
                }

                model.Params.Add(new RpcParam(parameter.Name, ser));
            }

            ReadAttribute(method, known, model);

            if (!check.BadSignature && check.BadParams.Count == 0 && !method.IsStatic && !method.IsGenericMethod && !method.IsAbstract)
            {
                check.Model = model;
            }

            return check;
        }

        private static void ReadAttribute(IMethodSymbol method, KnownSymbols known, RpcModel model)
        {
            model.TargetExpression = DefaultTarget;
            model.DeliveryExpression = DefaultDelivery;

            foreach (var data in method.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(data.AttributeClass, known.NetRpcAttribute))
                {
                    continue;
                }

                if (data.ConstructorArguments.Length > 0)
                {
                    model.TargetExpression = EnumExpression(data.ConstructorArguments[0], DefaultTarget);
                }

                foreach (var named in data.NamedArguments)
                {
                    if (named.Key == "Delivery")
                    {
                        model.DeliveryExpression = EnumExpression(named.Value, DefaultDelivery);
                    }
                    else if (named.Key == "OwnerOnly" && named.Value.Value is bool ownerOnly)
                    {
                        model.OwnerOnly = ownerOnly;
                    }
                }
            }
        }

        private static string EnumExpression(TypedConstant constant, string fallback)
        {
            if (constant.IsNull || !(constant.Type is INamedTypeSymbol enumType) || enumType.TypeKind != TypeKind.Enum)
            {
                return fallback;
            }

            var typeName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && Equals(member.ConstantValue, constant.Value))
                {
                    return typeName + "." + member.Name;
                }
            }

            return "(" + typeName + ")" + System.Convert.ToString(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static RpcSet BuildRpcSet(INamedTypeSymbol type, KnownSymbols known)
        {
            var set = new RpcSet();
            var names = new HashSet<string>();
            var ids = new Dictionary<ushort, RpcModel>();

            foreach (var model in ValidRpcs(type, known))
            {
                Add(set, set.Own, names, ids, model);
            }

            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (current.SpecialType == SpecialType.System_Object || (known.NetworkBehaviour != null && SymbolEqualityComparer.Default.Equals(current, known.NetworkBehaviour)))
                {
                    break;
                }

                foreach (var model in ValidRpcs(current, known))
                {
                    set.BaseHasRpc = true;

                    if (model.Method.DeclaredAccessibility != Accessibility.Private)
                    {
                        Add(set, set.Inherited, names, ids, model);
                    }
                }
            }

            return set;
        }

        private static void Add(RpcSet set, List<RpcModel> target, HashSet<string> names, Dictionary<ushort, RpcModel> ids, RpcModel model)
        {
            if (names.Contains(model.Name))
            {
                return;
            }

            if (ids.TryGetValue(model.Id, out var existing))
            {
                set.Collisions.Add(new KeyValuePair<RpcModel, RpcModel>(existing, model));
                return;
            }

            names.Add(model.Name);
            ids[model.Id] = model;
            target.Add(model);
        }

        private static IEnumerable<RpcModel> ValidRpcs(INamedTypeSymbol type, KnownSymbols known)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!IsRpc(method, known))
                {
                    continue;
                }

                var check = CheckRpc(method, known);

                if (check.IsValid)
                {
                    yield return check.Model;
                }
            }
        }

        public static List<SyncVarModel> SyncVars(INamedTypeSymbol type, KnownSymbols known)
        {
            var result = new List<SyncVarModel>();

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsImplicitlyDeclared || !known.IsSyncVarField(field, out var valueType))
                {
                    continue;
                }

                if (TypeSer.TryCreate(valueType, known, out var ser))
                {
                    result.Add(new SyncVarModel(field, ser));
                }
            }

            return result;
        }

        public static List<SerMember> SerializableMembers(INamedTypeSymbol type, KnownSymbols known)
        {
            var result = new List<SerMember>();

            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (member is IFieldSymbol field && !field.IsConst && !field.IsReadOnly)
                {
                    TypeSer.TryCreate(field.Type, known, out var ser);
                    result.Add(new SerMember(field, field.Type, ser));
                }
                else if (member is IPropertySymbol property && !property.IsIndexer && property.GetMethod != null && property.SetMethod != null && IsAutoProperty(property))
                {
                    TypeSer.TryCreate(property.Type, known, out var ser);
                    result.Add(new SerMember(property, property.Type, ser));
                }
            }

            return result;
        }

        private static bool IsAutoProperty(IPropertySymbol property)
        {
            foreach (var reference in property.DeclaringSyntaxReferences)
            {
                if (!(reference.GetSyntax() is PropertyDeclarationSyntax syntax) || syntax.ExpressionBody != null || syntax.AccessorList == null)
                {
                    return false;
                }

                foreach (var accessor in syntax.AccessorList.Accessors)
                {
                    if (accessor.Body != null || accessor.ExpressionBody != null)
                    {
                        return false;
                    }
                }

                return syntax.AccessorList.Accessors.Any(a => a.Kind() == SyntaxKind.SetAccessorDeclaration);
            }

            return false;
        }

        public static bool HasParameterlessConstructor(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Struct)
            {
                return true;
            }

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.Parameters.Length == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static string FullName(INamedTypeSymbol type)
        {
            var name = type.Name;

            for (var container = type.ContainingType; container != null; container = container.ContainingType)
            {
                name = container.Name + "+" + name;
            }

            var ns = type.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }
    }
}
