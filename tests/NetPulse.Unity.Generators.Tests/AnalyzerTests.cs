using Microsoft.CodeAnalysis;
using NetPulse.Unity.Generators;

namespace NetPulse.Unity.Generators.Tests;

public sealed class AnalyzerTests
{
    private const string Usings = "using System;\nusing System.Collections.Generic;\nusing NetPulse;\nusing UnityEngine;\n";

    private static string[] Ids(string body) => Harness.Run(Usings + body).AnalyzerIds;

    private static void AssertFires(string id, string body) => Assert.Contains(id, Ids(body));

    private static void AssertSilent(string id, string body) => Assert.DoesNotContain(id, Ids(body));

    [Fact]
    public void NP0001FiresForNonPartialTypesThatNeedGeneratedCode()
    {
        AssertFires("NP0001", "class A : NetworkBehaviour { [NetRpc] public void M() { } }");
        AssertFires("NP0001", "class B : NetworkBehaviour { readonly SyncVar<int> _a = new SyncVar<int>(); }");
        AssertFires("NP0001", "[NetSerializable] struct C { public int X; }");
        AssertFires("NP0001", "struct D : INetMessage { public int X; }");
        AssertFires("NP0001", "partial class Outer { class E : NetworkBehaviour { [NetRpc] public void M() { } } }");
        AssertFires("NP0001", "class Outer2 { partial class F : NetworkBehaviour { [NetRpc] public void M() { } } }");
        Assert.Equal(1, Harness.Run(Usings + "class Outer2 { partial class F : NetworkBehaviour { [NetRpc] public void M() { } } partial class G : NetworkBehaviour { [NetRpc] public void M() { } } }").Analyze().Count(d => d.Id == "NP0001"));
    }

    [Fact]
    public void NP0001IsSilentForPartialTypesAndPlainClasses()
    {
        AssertSilent("NP0001", "partial class A : NetworkBehaviour { [NetRpc] public void M() { } }");
        AssertSilent("NP0001", "partial class B : NetworkBehaviour { readonly SyncVar<int> _a = new SyncVar<int>(); }");
        AssertSilent("NP0001", "[NetSerializable] partial struct C { public int X; }");
        AssertSilent("NP0001", "class Plain { public int X; }");
        AssertSilent("NP0001", "class PlainBehaviour : NetworkBehaviour { }");
        AssertSilent("NP0001", "partial class Outer { partial class E : NetworkBehaviour { [NetRpc] public void M() { } } }");
    }

    [Fact]
    public void NP0002FiresForRpcOutsideNetworkBehaviour()
    {
        AssertFires("NP0002", "partial class A { [NetRpc] public void M() { } }");
        AssertFires("NP0002", "partial class B : MonoBehaviour { [NetRpc] public void M() { } }");
        AssertSilent("NP0002", "partial class C : NetworkBehaviour { [NetRpc] public void M() { } }");
    }

    [Fact]
    public void NP0003FiresForUnsupportedRpcParameterTypesAndListsSupportedOnes()
    {
        AssertFires("NP0003", "class Foo { } partial class A : NetworkBehaviour { [NetRpc] public void M(Foo f) { } }");
        AssertFires("NP0003", "partial class B : NetworkBehaviour { [NetRpc] public void M(object o) { } }");
        AssertFires("NP0003", "partial class C : NetworkBehaviour { [NetRpc] public void M(int? n) { } }");
        AssertFires("NP0003", "partial class D : NetworkBehaviour { [NetRpc] public void M(NetRpcInfo info, int later) { } }");
        AssertFires("NP0003", "partial class E : NetworkBehaviour { [NetRpc] public void M(Dictionary<int, int> d) { } }");
        AssertFires("NP0003", "partial class F : NetworkBehaviour { [NetRpc] public void M(int[,] d) { } }");
        AssertSilent("NP0003", "[NetSerializable] partial struct Ok { public int X; } partial class G : NetworkBehaviour { [NetRpc] public void M(Ok ok, List<Ok> list, Vector3 v, Player p, NetRpcInfo info) { } }");

        var diagnostic = Harness.Run(Usings + "class Foo { } partial class H : NetworkBehaviour { [NetRpc] public void M(Foo f) { } }").Analyze().Single(d => d.Id == "NP0003");
        var message = diagnostic.GetMessage();
        Assert.Contains("Foo", message);
        Assert.Contains("Vector3", message);
        Assert.Contains("[NetSerializable]", message);
    }

    [Fact]
    public void NP0004FiresForNonVoidReturnAndRefParameters()
    {
        AssertFires("NP0004", "partial class A : NetworkBehaviour { [NetRpc] public int M() => 1; }");
        AssertFires("NP0004", "partial class B : NetworkBehaviour { [NetRpc] public void M(ref int a) { } }");
        AssertFires("NP0004", "partial class C : NetworkBehaviour { [NetRpc] public void M(out int a) { a = 1; } }");
        AssertFires("NP0004", "partial class D : NetworkBehaviour { [NetRpc] public void M(in int a) { } }");
        AssertSilent("NP0004", "partial class E : NetworkBehaviour { [NetRpc] public void M(int a) { } }");
    }

    [Fact]
    public void NP0005FiresForRpcOverloads()
    {
        AssertFires("NP0005", "partial class A : NetworkBehaviour { [NetRpc] public void M(int a) { } [NetRpc] public void M(int a, int b) { } }");
        AssertFires("NP0005", "partial class B : NetworkBehaviour { [NetRpc] public void M(int a) { } public void M(string s) { } }");
        AssertSilent("NP0005", "partial class C : NetworkBehaviour { [NetRpc] public void M(int a) { } [NetRpc] public void N(int a) { } }");
    }

    [Fact]
    public void NP0006FiresForUnsupportedFieldTypesInSerializableTypesAndMessages()
    {
        AssertFires("NP0006", "class Foo { } [NetSerializable] partial struct A { public Foo F; }");
        AssertFires("NP0006", "partial struct B : INetMessage { public object O; }");
        AssertFires("NP0006", "[NetSerializable] partial class C { public Dictionary<int, int> D { get; set; } }");
        AssertFires("NP0006", "[NetSerializable] partial class D { public D(int x) { } public int X; }");
        AssertFires("NP0006", "[NetSerializable] partial struct E<T> { public int X; }");
        AssertSilent("NP0006", "[NetSerializable] partial struct F { public int X; public Vector3 V; public List<int> L; public int[] A; public Player P; }");
        AssertSilent("NP0006", "[NetSerializable] partial struct G { public int X; public readonly object Ignored; public object Computed => null; }");
        AssertSilent("NP0006", "[NetSerializable] partial struct Inner { public int X; } [NetSerializable] partial struct H { public Inner I; }");
    }

    [Fact]
    public void NP0007FiresForUnsupportedSyncVarTypes()
    {
        AssertFires("NP0007", "class Foo { } partial class A : NetworkBehaviour { readonly SyncVar<Foo> _f = new SyncVar<Foo>(); }");
        AssertFires("NP0007", "partial class B : NetworkBehaviour { readonly SyncVar<object> _f = new SyncVar<object>(); }");
        AssertSilent("NP0007", "[NetSerializable] partial struct Ok { public int X; } partial class C : NetworkBehaviour { readonly SyncVar<int> _a = new SyncVar<int>(); readonly SyncVar<Ok> _b = new SyncVar<Ok>(); readonly SyncVar<Vector3> _c = new SyncVar<Vector3>(); }");
    }

    [Fact]
    public void NP0008WarnsForNonReadonlySyncVarsAndSyncVarsOutsideBehaviours()
    {
        var mutable = Harness.Run(Usings + "partial class A : NetworkBehaviour { SyncVar<int> _a = new SyncVar<int>(); public int V => _a.Value; }").Analyze().Single(d => d.Id == "NP0008");
        Assert.Equal(DiagnosticSeverity.Warning, mutable.Severity);
        AssertFires("NP0008", "class B { readonly SyncVar<int> _a = new SyncVar<int>(); public int V => _a.Value; }");
        AssertSilent("NP0008", "partial class C : NetworkBehaviour { readonly SyncVar<int> _a = new SyncVar<int>(); }");
    }

    [Fact]
    public void NP0009FiresForRpcIdCollisionsWithinATypeAndAcrossTheHierarchy()
    {
        var first = FindCollidingNames(out var second);
        var inType = $"partial class A : NetworkBehaviour {{ [NetRpc] public void {first}() {{ }} [NetRpc] public void {second}() {{ }} }}";
        AssertFires("NP0009", inType);

        var inHierarchy = $"partial class B : NetworkBehaviour {{ [NetRpc] public void {first}() {{ }} }} partial class C : B {{ [NetRpc] public void {second}() {{ }} }}";
        AssertFires("NP0009", inHierarchy);

        AssertSilent("NP0009", "partial class D : NetworkBehaviour { [NetRpc] public void Alpha() { } [NetRpc] public void Beta() { } }");
    }

    [Fact]
    public void NP0009FiresForMessageIdCollisionsInTheAssembly()
    {
        var first = FindCollidingNames(out var second, "M");
        var colliding = $"namespace Game {{ partial struct {first} : INetMessage {{ public int X; }} [NetSerializable] partial struct {second} {{ public int X; }} }}";
        Assert.Contains("NP0009", Harness.Run(Usings + colliding).AnalyzerIds);

        var fine = "namespace Game { partial struct One : INetMessage { public int X; } [NetSerializable] partial struct Two { public int X; } }";
        Assert.DoesNotContain("NP0009", Harness.Run(Usings + fine).AnalyzerIds);
    }

    [Fact]
    public void NP0010FiresForSyncedFieldsOfUnsupportedTypes()
    {
        AssertFires("NP0010", "partial class A : NetworkBehaviour { [Synced] public Guid G; }");
        AssertFires("NP0010", "partial class B : NetworkBehaviour { [Synced] public List<int> L = new List<int>(); }");
        AssertFires("NP0010", "partial class C : NetworkBehaviour { [Synced] public Vector4 V; }");
        AssertSilent("NP0010", "enum E { A } partial class D : NetworkBehaviour { [Synced] public int I; [Synced] public float F; [Synced] public Vector3 V; [Synced] public E En; [Synced] public string S = \"\"; [Synced] public bool B; }");
    }

    [Fact]
    public void NP0011WarnsForStringCallRpcWhenAnRpcMethodWithThatNameExists()
    {
        const string body = "partial class A : NetworkBehaviour { [NetRpc] public void Hit(int d) { } public void T() { CallRpc(\"Hit\", RpcTarget.All, 1); CallRpc(nameof(Hit), RpcTarget.All, 1); } }";
        var diagnostics = Harness.Run(Usings + body).Analyze().Where(d => d.Id == "NP0011").ToArray();

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("Rpc.Hit(", diagnostics[0].GetMessage());

        AssertSilent("NP0011", "partial class B : NetworkBehaviour { public void Other(int d) { } public void T() { CallRpc(\"Other\", RpcTarget.All, 1); CallRpc(\"Missing\", RpcTarget.All); } }");
        AssertSilent("NP0011", "partial class C : NetworkBehaviour { [NetRpc] public void Hit(int d) { } public void T(string dynamicName) { CallRpc(dynamicName, RpcTarget.All, 1); } }");
        AssertFires("NP0011", "partial class D : NetworkBehaviour { [NetRpc] public void Hit(int d) { } } partial class E : NetworkBehaviour { public void T(D other) { other.CallRpc(\"Hit\", RpcTarget.All, 1); } }");
    }

    [Fact]
    public void ValidCodeProducesNoAnalyzerDiagnosticsAtAll()
    {
        const string body = """
            enum Mode { A, B }

            [NetSerializable]
            partial struct Point { public int X; public int Y { get; set; } }

            partial struct Chat : INetMessage { public string Text; public Point At; }

            partial class Base : NetworkBehaviour
            {
                readonly SyncVar<int> _hp = new SyncVar<int>(10);
                readonly SyncVar<Point> _pos = new SyncVar<Point>(), SyncPermission.Master);

                [Synced] public float Speed = 1f;

                [NetRpc(RpcTarget.Others, OwnerOnly = true)]
                public void Hit(int damage, Mode mode, Point[] points, List<Vector3> path, NetRpcInfo info) { }
            }

            partial class Derived : Base
            {
                [NetRpc]
                public void Extra(Player who, NetworkObject target) { }

                public void T() { Rpc.Hit(1, Mode.A, null, null); Rpc.To(RpcTarget.All).Extra(null, null); }
            }
            """;

        var result = Harness.Run(Usings + body.Replace("new SyncVar<Point>(), SyncPermission.Master)", "new SyncVar<Point>(default, SyncPermission.Master)"));
        Assert.Empty(result.CompileProblems);
        Assert.Empty(result.Analyze());
    }

    private static string FindCollidingNames(out string other, string prefix = "R")
    {
        var seen = new Dictionary<ushort, string>();

        for (var i = 0; i < 2_000_000; i++)
        {
            var name = prefix + i;
            var id = StableId.Compute(prefix == "R" ? name : "Game." + name);

            if (seen.TryGetValue(id, out var previous))
            {
                other = name;
                return previous;
            }

            seen[id] = name;
        }

        throw new InvalidOperationException("No collision found");
    }
}
