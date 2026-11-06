# IL2CPP

NetPulse works with IL2CPP and managed code stripping at any stripping level. You do not need `[Preserve]` attributes or your own `link.xml` for `[NetRpc]` methods, `[Synced]` fields, `NetworkBehaviour` subclasses, `INetSerializable` types, `[NetSerializable]` types or `INetMessage` types. Two mechanisms keep them alive.

## Layer 1: the attributes

`[NetRpc]`, `[Synced]` and `[NetSerializable]` derive from `UnityEngine.Scripting.PreserveAttribute`. Unity documents that attribute as subclassable and honored by the linker, so a member marked with them survives stripping by itself.

## Layer 2: a generated link.xml

At build time the editor class `NetPulseLinkerProcessor` (an `IUnityLinkerProcessor`) finds your code through Unity's `TypeCache` and writes `Library/NetPulse/link.xml`. Unity's linker reads it together with its own files. The processor looks at the player assemblies without test assemblies, and for each of your assemblies it preserves:

- every method marked `[NetRpc]`, with its signature
- every `[Synced]` field
- every non-abstract `NetworkBehaviour` subclass, with its `OnSerialize` override and the generated `RegisterSyncVars` and `InvokeGeneratedRpc` methods
- every type that implements `INetSerializable`
- every `[NetSerializable]` type and every `INetMessage` type, with their generated `NetWrite` and `NetRead` methods

The three package assemblies, `NetPulse.Runtime`, `NetPulse.Common` and `NetPulse.LiteNetLib`, are preserved completely. They need an explicit entry because Unity does not read `link.xml` files that live inside packages.

The shape of the result:

```xml
<linker>
  <assembly fullname="NetPulse.Runtime" preserve="all" />
  <assembly fullname="NetPulse.Common" preserve="all" />
  <assembly fullname="NetPulse.LiteNetLib" preserve="all" />
  <assembly fullname="Assembly-CSharp">
    <type fullname="Hero">
      <method signature="System.Void Hit(System.Int32,UnityEngine.Vector3,NetPulse.NetRpcInfo)" />
      <method name="InvokeGeneratedRpc" />
    </type>
  </assembly>
</linker>
```

## Previewing it

Choose `NetPulse > Tools > Preview IL2CPP link.xml`. The editor writes the same content that a build would use to `Library/NetPulse/link.preview.xml`, prints it to the console and reveals the file. Use it to check that a type you care about is listed, for example after moving code into a new assembly definition.

A type is listed only if its assembly is part of the player build. Editor-only assemblies and test assemblies are left out on purpose.

## When something is still missing

If a reflection-based call cannot find an RPC method, the error names the type and the method and points back to this mechanism. In practice this means a custom build step bypassed the NetPulse linker processor (for example a build pipeline that replaces the additional link files). Check the preview and make sure the `Library/NetPulse/link.xml` is passed to the linker.

For generated RPCs, a missing dispatch usually has a different cause: the class is not `partial`, or the source generator is not active for the assembly. The error then says so. See [analyzers](analyzers.md) and [troubleshooting](../troubleshooting.md).
