using ChaCha20Poly1305 = NetPulse.Common.Security.ChaCha20Poly1305;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NetPulse.Common.Security;
using Org.BouncyCastle.Crypto.Parameters;

namespace NetPulse.Common.Tests;

public sealed class CryptoPrimitiveTests
{
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static byte[] Run(byte[] scalar, byte[] u)
    {
        var result = new byte[32];
        X25519.ScalarMult(scalar, u, result);
        return result;
    }

    private static byte[] BouncyScalarMult(byte[] scalar, byte[] u)
    {
        var result = new byte[32];
        Org.BouncyCastle.Math.EC.Rfc7748.X25519.ScalarMult(scalar, 0, u, 0, result, 0);
        return result;
    }

    private static byte[] ReferenceScalarMult(byte[] scalar, byte[] u)
    {
        var k = (byte[])scalar.Clone();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        var kValue = new BigInteger(k, isUnsigned: true, isBigEndian: false);
        var uBytes = (byte[])u.Clone();
        uBytes[31] &= 127;
        var x1 = new BigInteger(uBytes, isUnsigned: true, isBigEndian: false) % Prime;
        BigInteger x2 = 1;
        BigInteger z2 = 0;
        var x3 = x1;
        BigInteger z3 = 1;
        var swap = false;

        for (var t = 254; t >= 0; t--)
        {
            var bit = ((kValue >> t) & 1) == 1;

            if (swap != bit)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = bit;
            var a = (x2 + z2) % Prime;
            var aa = a * a % Prime;
            var b = (x2 - z2 + Prime) % Prime;
            var bb = b * b % Prime;
            var e = (aa - bb + Prime) % Prime;
            var c = (x3 + z3) % Prime;
            var d = (x3 - z3 + Prime) % Prime;
            var da = d * a % Prime;
            var cb = c * b % Prime;
            var sum = (da + cb) % Prime;
            var difference = (da - cb + Prime) % Prime;
            x3 = sum * sum % Prime;
            z3 = x1 * (difference * difference % Prime) % Prime;
            x2 = aa * bb % Prime;
            z2 = e * ((aa + 121665 * e) % Prime) % Prime;
        }

        if (swap)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        var value = x2 * BigInteger.ModPow(z2, Prime - 2, Prime) % Prime;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        Array.Copy(bytes, padded, bytes.Length);
        return padded;
    }

    [Fact]
    public void X25519MatchesRfc7748Vectors()
    {
        Assert.Equal(
            Hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"),
            Run(Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"), Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")));
        Assert.Equal(
            Hex("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957"),
            Run(Hex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"), Hex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493")));
    }

    [Fact]
    public void X25519MatchesRfc7748IteratedVectors()
    {
        var k = new byte[32];
        k[0] = 9;
        var u = (byte[])k.Clone();

        for (var i = 1; i <= 1000; i++)
        {
            var next = Run(k, u);
            u = k;
            k = next;

            if (i == 1)
            {
                Assert.Equal(Hex("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"), k);
            }
        }

        Assert.Equal(Hex("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51"), k);
    }

    [Fact]
    public void X25519DiffieHellmanMatchesRfc7748()
    {
        var alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePublic = X25519.GetPublicKey(alicePrivate);
        var bobPublic = X25519.GetPublicKey(bobPrivate);
        Assert.Equal(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), alicePublic);
        Assert.Equal(Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"), bobPublic);

        var sharedA = new byte[32];
        var sharedB = new byte[32];
        Assert.True(X25519.TryAgree(alicePrivate, bobPublic, sharedA));
        Assert.True(X25519.TryAgree(bobPrivate, alicePublic, sharedB));
        Assert.Equal(Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"), sharedA);
        Assert.Equal(sharedA, sharedB);
    }

    [Fact]
    public void X25519MatchesBouncyCastleAndBigIntegerOnRandomInputs()
    {
        var random = new Random(12345);

        for (var i = 0; i < 200; i++)
        {
            var scalar = new byte[32];
            var u = new byte[32];
            random.NextBytes(scalar);
            random.NextBytes(u);
            var actual = Run(scalar, u);
            Assert.Equal(BouncyScalarMult(scalar, u), actual);

            if (i < 40)
            {
                Assert.Equal(ReferenceScalarMult(scalar, u), actual);
            }
        }
    }

    [Fact]
    public void X25519HandlesEdgeCoordinates()
    {
        var random = new Random(77);
        var edges = new List<byte[]>();

        foreach (var text in new[] { "00", "01", "02", "09" })
        {
            var edge = new byte[32];
            edge[0] = Convert.ToByte(text, 16);
            edges.Add(edge);
        }

        var primeBytes = Prime.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        Array.Copy(primeBytes, padded, primeBytes.Length);
        edges.Add(padded);
        edges.Add(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        var minusOne = (byte[])padded.Clone();
        minusOne[0] -= 1;
        edges.Add(minusOne);
        var plusOne = (byte[])padded.Clone();
        plusOne[0] += 1;
        edges.Add(plusOne);
        var highBit = new byte[32];
        highBit[31] = 0x80;
        edges.Add(highBit);

        foreach (var edge in edges)
        {
            var scalar = new byte[32];
            random.NextBytes(scalar);
            var actual = Run(scalar, edge);
            Assert.Equal(BouncyScalarMult(scalar, edge), actual);
            Assert.Equal(ReferenceScalarMult(scalar, edge), actual);
        }

        var zero = new byte[32];
        var scalarForZero = new byte[32];
        random.NextBytes(scalarForZero);
        Assert.False(X25519.TryAgree(scalarForZero, zero, new byte[32]));
        Assert.False(X25519.TryAgree(scalarForZero, edges[1], new byte[32]));
    }

    [Fact]
    public void ChaCha20BlockMatchesRfc8439()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = Hex("000000090000004a00000000");
        var block = new byte[64];
        ChaCha20.Block(key, nonce, 1, block);
        Assert.Equal(Hex("10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e"), block.Take(32).ToArray());
    }

    [Fact]
    public void AeadMatchesRfc8439Vector()
    {
        var key = Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray();
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var plaintext = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var data = (byte[])plaintext.Clone();
        var tag = new byte[16];
        ChaCha20Poly1305.Encrypt(key, nonce, aad, data, tag);
        Assert.Equal(Hex("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116"), data);
        Assert.Equal(Hex("1ae10b594f09e26a7e902ecbd0600691"), tag);
        Assert.True(ChaCha20Poly1305.Decrypt(key, nonce, aad, data, tag));
        Assert.Equal(plaintext, data);
    }

    [Fact]
    public void AeadMatchesBouncyCastleOnRandomInputs()
    {
        var random = new Random(99);

        for (var i = 0; i < 300; i++)
        {
            var key = new byte[32];
            var nonce = new byte[12];
            var aad = new byte[random.Next(0, 40)];
            var plaintext = new byte[random.Next(0, 400)];
            random.NextBytes(key);
            random.NextBytes(nonce);
            random.NextBytes(aad);
            random.NextBytes(plaintext);

            var data = (byte[])plaintext.Clone();
            var tag = new byte[16];
            ChaCha20Poly1305.Encrypt(key, nonce, aad, data, tag);

            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
            var expected = new byte[cipher.GetOutputSize(plaintext.Length)];
            var written = cipher.ProcessBytes(plaintext, 0, plaintext.Length, expected, 0);
            cipher.DoFinal(expected, written);

            Assert.Equal(expected.Take(plaintext.Length).ToArray(), data);
            Assert.Equal(expected.Skip(plaintext.Length).ToArray(), tag);

            if (ChaCha20Poly1305Bcl.IsSupported)
            {
                using var bcl = new ChaCha20Poly1305Bcl(key);
                var bclCipher = new byte[plaintext.Length];
                var bclTag = new byte[16];
                bcl.Encrypt(nonce, plaintext, bclCipher, bclTag, aad);
                Assert.Equal(bclCipher, data);
                Assert.Equal(bclTag, tag);
            }

            Assert.True(ChaCha20Poly1305.Decrypt(key, nonce, aad, data, tag));
            Assert.Equal(plaintext, data);
        }
    }

    [Fact]
    public void AeadRejectsEveryModifiedByte()
    {
        var random = new Random(5);
        var key = new byte[32];
        var nonce = new byte[12];
        var aad = new byte[10];
        var plaintext = new byte[100];
        random.NextBytes(key);
        random.NextBytes(nonce);
        random.NextBytes(aad);
        random.NextBytes(plaintext);
        var data = (byte[])plaintext.Clone();
        var tag = new byte[16];
        ChaCha20Poly1305.Encrypt(key, nonce, aad, data, tag);

        for (var i = 0; i < data.Length; i++)
        {
            var copy = (byte[])data.Clone();
            copy[i] ^= 0x01;
            Assert.False(ChaCha20Poly1305.Decrypt(key, nonce, aad, copy, tag));
            Assert.Equal(data.Length, copy.Length);
        }

        for (var i = 0; i < aad.Length; i++)
        {
            var copy = (byte[])aad.Clone();
            copy[i] ^= 0x80;
            Assert.False(ChaCha20Poly1305.Decrypt(key, nonce, copy, (byte[])data.Clone(), tag));
        }

        for (var i = 0; i < tag.Length; i++)
        {
            var copy = (byte[])tag.Clone();
            copy[i] ^= 0x01;
            Assert.False(ChaCha20Poly1305.Decrypt(key, nonce, aad, (byte[])data.Clone(), copy));
        }
    }

    [Fact]
    public void FailedDecryptLeavesCiphertextUntouched()
    {
        var key = new byte[32];
        var nonce = new byte[12];
        var data = new byte[64];
        new Random(1).NextBytes(data);
        var copy = (byte[])data.Clone();
        Assert.False(ChaCha20Poly1305.Decrypt(key, nonce, ReadOnlySpan<byte>.Empty, copy, new byte[16]));
        Assert.Equal(data, copy);
    }

    [Fact]
    public void HkdfMatchesRfc5869Vectors()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Enumerable.Range(0, 13).Select(i => (byte)i).ToArray();
        var info = Enumerable.Range(0xf0, 10).Select(i => (byte)i).ToArray();
        var prk = new byte[32];
        Hkdf.Extract(salt, ikm, prk);
        Assert.Equal(Hex("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5"), prk);
        var okm = new byte[42];
        Hkdf.Expand(prk, info, okm);
        Assert.Equal(Hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"), okm);

        var prk3 = new byte[32];
        Hkdf.Extract(ReadOnlySpan<byte>.Empty, ikm, prk3);
        Assert.Equal(Hex("19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04"), prk3);
        var okm3 = new byte[42];
        Hkdf.Expand(prk3, ReadOnlySpan<byte>.Empty, okm3);
        Assert.Equal(Hex("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"), okm3);
    }

    [Fact]
    public void HmacMatchesBclOnRandomInputs()
    {
        var random = new Random(8);

        for (var i = 0; i < 200; i++)
        {
            var key = new byte[random.Next(0, 100)];
            var message = new byte[random.Next(0, 200)];
            random.NextBytes(key);
            random.NextBytes(message);
            var actual = new byte[32];
            HmacSha256.Compute(key, message, actual);
            Assert.Equal(HMACSHA256.HashData(key, message), actual);
        }
    }

    [Fact]
    public void HmacMatchesRfc4231Vector()
    {
        var actual = new byte[32];
        HmacSha256.Compute(Enumerable.Repeat((byte)0x0b, 20).ToArray(), Encoding.ASCII.GetBytes("Hi There"), actual);
        Assert.Equal(Hex("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"), actual);
    }

    [Fact]
    public void CryptoRoutinesAreAllocationFreeAfterWarmup()
    {
        var key = new byte[32];
        var nonce = new byte[12];
        var aad = new byte[10];
        var data = new byte[1200];
        var tag = new byte[16];
        ChaCha20Poly1305.Encrypt(key, nonce, aad, data, tag);
        ChaCha20Poly1305.Decrypt(key, nonce, aad, data, tag);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            ChaCha20Poly1305.Encrypt(key, nonce, aad, data, tag);
            ChaCha20Poly1305.Decrypt(key, nonce, aad, data, tag);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class ChaCha20Poly1305Bcl : IDisposable
    {
        private readonly System.Security.Cryptography.ChaCha20Poly1305 _inner;

        public ChaCha20Poly1305Bcl(byte[] key) => _inner = new System.Security.Cryptography.ChaCha20Poly1305(key);

        public static bool IsSupported => System.Security.Cryptography.ChaCha20Poly1305.IsSupported;

        public void Encrypt(byte[] nonce, byte[] plaintext, byte[] cipher, byte[] tag, byte[] aad) => _inner.Encrypt(nonce, plaintext, cipher, tag, aad);

        public void Dispose() => _inner.Dispose();
    }
}
