using Microsoft.CodeAnalysis;

namespace NetPulse.Unity.Generators
{
    public static class Descriptors
    {
        private const string Category = "NetPulse";

        public static readonly DiagnosticDescriptor NotPartial = Create(
            "NP0001",
            "Type must be partial",
            "Type '{0}' must be declared 'partial' because {1}. Add the 'partial' modifier to the declaration.",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor RpcOutsideBehaviour = Create(
            "NP0002",
            "[NetRpc] outside NetworkBehaviour",
            "[NetRpc] method '{0}' is declared in '{1}', which does not derive from NetworkBehaviour. Derive '{1}' from NetworkBehaviour or remove [NetRpc].",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedRpcParameter = Create(
            "NP0003",
            "Unsupported RPC parameter type",
            "RPC '{0}' parameter '{1}' has unsupported type '{2}'. {3}",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor InvalidRpcSignature = Create(
            "NP0004",
            "Invalid RPC signature",
            "RPC '{0}' must return void and must not have ref, out or in parameters. Change the return type to void and pass parameters by value.",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor RpcOverload = Create(
            "NP0005",
            "RPC overloads are not allowed",
            "RPC '{0}' has an overload with the same name. RPC ids are derived from the method name, so rename one of the overloads.",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedMember = Create(
            "NP0006",
            "Unsupported member in serializable type",
            "Member '{0}' of '{1}' cannot be serialized: {2}",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedSyncVar = Create(
            "NP0007",
            "Unsupported SyncVar type",
            "SyncVar<{0}> in '{1}' is not supported. Supported types: {2}. Mark a custom type with [NetSerializable] (it must be partial).",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor SyncVarShape = Create(
            "NP0008",
            "SyncVar field should be readonly and inside a NetworkBehaviour",
            "SyncVar field '{0}' {1}",
            DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor IdCollision = Create(
            "NP0009",
            "Stable id collision",
            "{0}",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedSynced = Create(
            "NP0010",
            "Unsupported [Synced] field type",
            "[Synced] field '{0}' has unsupported type '{1}'. Supported: bool, byte, short, int, long, float, double, string, Vector2, Vector3, Quaternion, Color, Vector2Int, Vector3Int and enums. Use SyncVar<T> for other types.",
            DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor StringRpcCall = Create(
            "NP0011",
            "Use the generated Rpc API",
            "CallRpc(\"{0}\", ...) can be replaced by Rpc.{0}(...), which is type-checked and avoids reflection.",
            DiagnosticSeverity.Warning);

        public static readonly System.Collections.Immutable.ImmutableArray<DiagnosticDescriptor> All = System.Collections.Immutable.ImmutableArray.Create(
            NotPartial,
            RpcOutsideBehaviour,
            UnsupportedRpcParameter,
            InvalidRpcSignature,
            RpcOverload,
            UnsupportedMember,
            UnsupportedSyncVar,
            SyncVarShape,
            IdCollision,
            UnsupportedSynced,
            StringRpcCall);

        private static DiagnosticDescriptor Create(string id, string title, string message, DiagnosticSeverity severity) =>
            new DiagnosticDescriptor(id, title, message, Category, severity, true);
    }
}
