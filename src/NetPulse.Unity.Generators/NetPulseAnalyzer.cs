using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NetPulse.Unity.Generators
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NetPulseAnalyzer : DiagnosticAnalyzer
    {
        private const string GeneratedSuffix = ".NetPulse.g.cs";

        private static readonly HashSet<string> SyncedUnityTypes = new HashSet<string>
        {
            "Vector2",
            "Vector3",
            "Quaternion",
            "Color",
            "Vector2Int",
            "Vector3Int"
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var known = KnownSymbols.Create(context.Compilation);

            if (!known.IsUsable)
            {
                return;
            }

            var state = new State(known);
            context.RegisterSymbolAction(c => AnalyzeType(c, state), SymbolKind.NamedType);
            context.RegisterOperationAction(c => AnalyzeInvocation(c, known), OperationKind.Invocation);
            context.RegisterCompilationEndAction(c => ReportIdCollisions(c, state));
        }

        private static void AnalyzeType(SymbolAnalysisContext context, State state)
        {
            var known = state.Known;

            if (!(context.Symbol is INamedTypeSymbol type) || (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct))
            {
                return;
            }

            var isBehaviour = known.IsNetworkBehaviour(type);
            var rpcMethods = type.GetMembers().OfType<IMethodSymbol>().Where(m => Discovery.IsRpc(m, known)).ToList();
            var syncFields = new List<KeyValuePair<IFieldSymbol, ITypeSymbol>>();

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.IsStatic && !field.IsImplicitlyDeclared && known.IsSyncVarField(field, out var valueType))
                {
                    syncFields.Add(new KeyValuePair<IFieldSymbol, ITypeSymbol>(field, valueType));
                }
            }

            var isSerializable = known.IsSerializableType(type) && !type.IsAbstract;
            var reasons = new List<string>();

            if (rpcMethods.Count > 0)
            {
                reasons.Add("it declares [NetRpc] methods");
            }

            if (isBehaviour && syncFields.Count > 0)
            {
                reasons.Add("it declares SyncVar<T> fields");
            }

            if (isSerializable)
            {
                reasons.Add("it is marked [NetSerializable] or implements INetMessage");
            }

            if (reasons.Count > 0)
            {
                CheckPartial(context, state, type, string.Join(" and ", reasons));
            }

            AnalyzeRpcs(context, known, type, rpcMethods, isBehaviour);
            AnalyzeSyncVars(context, known, type, syncFields, isBehaviour);
            AnalyzeSynced(context, known, type);

            if (isSerializable)
            {
                AnalyzeSerializable(context, known, type, state);
            }
        }

        private static void CheckPartial(SymbolAnalysisContext context, State state, INamedTypeSymbol type, string reason)
        {
            if (!IsPartial(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotPartial, IdentifierLocation(type), type.Name, reason));
            }

            for (var container = type.ContainingType; container != null; container = container.ContainingType)
            {
                if (!IsPartial(container) && state.ReportedContainers.TryAdd(container, true))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotPartial, IdentifierLocation(container), container.Name, "it contains the nested type '" + type.Name + "' that needs generated code"));
                }
            }
        }

        private static void AnalyzeRpcs(SymbolAnalysisContext context, KnownSymbols known, INamedTypeSymbol type, List<IMethodSymbol> rpcMethods, bool isBehaviour)
        {
            foreach (var method in rpcMethods)
            {
                var location = method.Locations.FirstOrDefault() ?? Location.None;

                if (!isBehaviour)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.RpcOutsideBehaviour, location, method.Name, type.Name));
                    continue;
                }

                var check = Discovery.CheckRpc(method, known);

                if (check.BadSignature)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.InvalidRpcSignature, location, method.Name));
                }

                foreach (var issue in check.BadParams)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnsupportedRpcParameter,
                        issue.Parameter.Locations.FirstOrDefault() ?? location,
                        method.Name,
                        issue.Parameter.Name,
                        issue.Parameter.Type.ToDisplayString(),
                        issue.Hint));
                }

                var overloads = type.GetMembers(method.Name).OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary);

                if (overloads > 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.RpcOverload, location, method.Name));
                }
            }

            if (!isBehaviour)
            {
                return;
            }

            var set = Discovery.BuildRpcSet(type, known);

            foreach (var pair in set.Collisions)
            {
                var first = pair.Key;
                var second = pair.Value;
                var inType = SymbolEqualityComparer.Default.Equals(second.Method.ContainingType, type) ? second : SymbolEqualityComparer.Default.Equals(first.Method.ContainingType, type) ? first : null;

                if (inType == null)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.IdCollision,
                    inType.Method.Locations.FirstOrDefault() ?? Location.None,
                    "RPC '" + first.Name + "' and RPC '" + second.Name + "' share the stable id " + first.Id + ". Rename one of the methods."));
            }
        }

        private static void AnalyzeSyncVars(SymbolAnalysisContext context, KnownSymbols known, INamedTypeSymbol type, List<KeyValuePair<IFieldSymbol, ITypeSymbol>> fields, bool isBehaviour)
        {
            foreach (var pair in fields)
            {
                var field = pair.Key;
                var location = field.Locations.FirstOrDefault() ?? Location.None;

                if (!TypeSer.TryCreate(pair.Value, known, out _))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedSyncVar, location, pair.Value.ToDisplayString(), type.Name, TypeSer.SupportedList));
                }

                if (!isBehaviour)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.SyncVarShape, location, field.Name, "is declared outside a NetworkBehaviour and will never be synchronized. Move it into a class that derives from NetworkBehaviour."));
                }
                else if (!field.IsReadOnly)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.SyncVarShape, location, field.Name, "should be declared 'readonly'. Reassigning the field would detach it from the network. Add the 'readonly' modifier."));
                }
            }
        }

        private static void AnalyzeSynced(SymbolAnalysisContext context, KnownSymbols known, INamedTypeSymbol type)
        {
            if (known.SyncedAttribute == null)
            {
                return;
            }

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.IsImplicitlyDeclared && known.HasAttribute(field, known.SyncedAttribute) && !IsSyncedSupported(field.Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedSynced, field.Locations.FirstOrDefault() ?? Location.None, field.Name, field.Type.ToDisplayString()));
                }
            }
        }

        private static bool IsSyncedSupported(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_String:
                    return true;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            return type.ContainingNamespace != null && type.ContainingNamespace.ToDisplayString() == "UnityEngine" && type.TypeKind == TypeKind.Struct && SyncedUnityTypes.Contains(type.Name);
        }

        private static void AnalyzeSerializable(SymbolAnalysisContext context, KnownSymbols known, INamedTypeSymbol type, State state)
        {
            if (type.IsGenericType)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedMember, IdentifierLocation(type), type.Name, type.Name, "generic types are not supported. Use a non-generic type."));
                return;
            }

            if (type.TypeKind == TypeKind.Class && !Discovery.HasParameterlessConstructor(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.UnsupportedMember, IdentifierLocation(type), type.Name + "()", type.Name, "classes need a parameterless constructor so the generated NetRead can create instances. Add one or use a struct."));
            }

            foreach (var member in Discovery.SerializableMembers(type, known))
            {
                if (member.Ser == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnsupportedMember,
                        member.Symbol.Locations.FirstOrDefault() ?? Location.None,
                        member.Name,
                        type.Name,
                        "type '" + member.Type.ToDisplayString() + "' is not supported. Supported types: " + TypeSer.SupportedList + "."));
                }
            }

            var name = Discovery.FullName(type);
            state.SerializableIds.Add(new IdEntry(StableId.Compute(name), name, IdentifierLocation(type)));
        }

        private static void ReportIdCollisions(CompilationAnalysisContext context, State state)
        {
            foreach (var group in state.SerializableIds.GroupBy(e => e.Id))
            {
                var entries = group.GroupBy(e => e.Name).Select(g => g.First()).OrderBy(e => e.Name, System.StringComparer.Ordinal).ToList();

                if (entries.Count < 2)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    var others = string.Join(", ", entries.Where(e => e != entry).Select(e => "'" + e.Name + "'"));
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.IdCollision,
                        entry.Location,
                        "Type '" + entry.Name + "' has the same stable id (" + group.Key + ") as " + others + ". Rename or move one of the types."));
                }
            }
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, KnownSymbols known)
        {
            var invocation = (IInvocationOperation)context.Operation;

            if (known.NetworkBehaviour == null || invocation.TargetMethod.Name != "CallRpc" || invocation.Arguments.Length == 0)
            {
                return;
            }

            var owner = invocation.TargetMethod.ContainingType;

            if (!SymbolEqualityComparer.Default.Equals(owner, known.NetworkBehaviour) && !known.IsNetworkBehaviour(owner))
            {
                return;
            }

            var argument = invocation.Arguments[0].Value;

            if (!argument.ConstantValue.HasValue || !(argument.ConstantValue.Value is string name))
            {
                return;
            }

            var receiver = invocation.Instance?.Type as INamedTypeSymbol ?? context.ContainingSymbol.ContainingType;

            for (var current = receiver; current != null; current = current.BaseType)
            {
                foreach (var method in current.GetMembers(name).OfType<IMethodSymbol>())
                {
                    if (Discovery.IsRpc(method, known) && Discovery.CheckRpc(method, known).IsValid)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.StringRpcCall, argument.Syntax.GetLocation(), name));
                        return;
                    }
                }
            }
        }

        private static IEnumerable<TypeDeclarationSyntax> UserDeclarations(INamedTypeSymbol type)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax declaration && !reference.SyntaxTree.FilePath.EndsWith(GeneratedSuffix, System.StringComparison.Ordinal))
                {
                    yield return declaration;
                }
            }
        }

        private static bool IsPartial(INamedTypeSymbol type)
        {
            var any = false;

            foreach (var declaration in UserDeclarations(type))
            {
                any = true;

                if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    return true;
                }
            }

            return !any;
        }

        private static Location IdentifierLocation(INamedTypeSymbol type)
        {
            foreach (var declaration in UserDeclarations(type))
            {
                return declaration.Identifier.GetLocation();
            }

            return type.Locations.FirstOrDefault() ?? Location.None;
        }

        private sealed class IdEntry
        {
            public IdEntry(ushort id, string name, Location location)
            {
                Id = id;
                Name = name;
                Location = location;
            }

            public ushort Id { get; }

            public string Name { get; }

            public Location Location { get; }
        }

        private sealed class State
        {
            public State(KnownSymbols known) => Known = known;

            public KnownSymbols Known { get; }

            public ConcurrentBag<IdEntry> SerializableIds { get; } = new ConcurrentBag<IdEntry>();

            public ConcurrentDictionary<INamedTypeSymbol, bool> ReportedContainers { get; } = new ConcurrentDictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
        }
    }
}
