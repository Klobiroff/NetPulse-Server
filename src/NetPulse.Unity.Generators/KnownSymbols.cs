using Microsoft.CodeAnalysis;

namespace NetPulse.Unity.Generators
{
    internal sealed class KnownSymbols
    {
        private KnownSymbols(Compilation compilation)
        {
            NetRpcAttribute = compilation.GetTypeByMetadataName("NetPulse.NetRpcAttribute");
            NetSerializableAttribute = compilation.GetTypeByMetadataName("NetPulse.NetSerializableAttribute");
            NetMessage = compilation.GetTypeByMetadataName("NetPulse.INetMessage");
            SyncVar = compilation.GetTypeByMetadataName("NetPulse.SyncVar`1");
            SyncedAttribute = compilation.GetTypeByMetadataName("NetPulse.SyncedAttribute");
            NetworkBehaviour = compilation.GetTypeByMetadataName("NetPulse.NetworkBehaviour");
            NetRpcInfo = compilation.GetTypeByMetadataName("NetPulse.NetRpcInfo");
            Player = compilation.GetTypeByMetadataName("NetPulse.Player");
            NetworkObject = compilation.GetTypeByMetadataName("NetPulse.NetworkObject");
            ListDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
            Guid = compilation.GetTypeByMetadataName("System.Guid");
        }

        public INamedTypeSymbol NetRpcAttribute { get; }

        public INamedTypeSymbol NetSerializableAttribute { get; }

        public INamedTypeSymbol NetMessage { get; }

        public INamedTypeSymbol SyncVar { get; }

        public INamedTypeSymbol SyncedAttribute { get; }

        public INamedTypeSymbol NetworkBehaviour { get; }

        public INamedTypeSymbol NetRpcInfo { get; }

        public INamedTypeSymbol Player { get; }

        public INamedTypeSymbol NetworkObject { get; }

        public INamedTypeSymbol ListDefinition { get; }

        public INamedTypeSymbol Guid { get; }

        public bool IsUsable => NetRpcAttribute != null || NetSerializableAttribute != null || NetMessage != null || SyncVar != null || SyncedAttribute != null;

        public static KnownSymbols Create(Compilation compilation) => new KnownSymbols(compilation);

        public bool IsNetworkBehaviour(INamedTypeSymbol type)
        {
            if (NetworkBehaviour == null)
            {
                return false;
            }

            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, NetworkBehaviour))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
        {
            if (attribute == null)
            {
                return false;
            }

            foreach (var data in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsSerializableType(INamedTypeSymbol type)
        {
            if (HasAttribute(type, NetSerializableAttribute))
            {
                return true;
            }

            return ImplementsNetMessage(type);
        }

        public bool ImplementsNetMessage(INamedTypeSymbol type)
        {
            if (NetMessage == null)
            {
                return false;
            }

            foreach (var candidate in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, NetMessage))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsSyncVarField(IFieldSymbol field, out ITypeSymbol valueType)
        {
            valueType = null;

            if (SyncVar != null && field.Type is INamedTypeSymbol named && named.IsGenericType && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, SyncVar))
            {
                valueType = named.TypeArguments[0];
                return true;
            }

            return false;
        }
    }
}
