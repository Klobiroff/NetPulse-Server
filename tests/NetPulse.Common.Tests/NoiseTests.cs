using System.Security.Cryptography;
using System.Text;
using NetPulse.Common.Security;
using Org.BouncyCastle.Crypto.Parameters;

namespace NetPulse.Common.Tests;

public sealed class NoiseTests
{
    private static byte[] Fill(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandshakeMatchesIndependentReference(bool usePreSharedKey)
    {
        var serverPrivate = Fill(1);
        var serverPublic = X25519.GetPublicKey(serverPrivate);
        var clientEphemeral = Fill(40);
        var serverEphemeral = Fill(90);
        var psk = usePreSharedKey ? Fill(200) : Array.Empty<byte>();
        var prologue = new byte[] { 0x1F, 1, 9, 8, 7, 6 };

        using var initiator = NoiseHandshake.CreateInitiator(serverPublic, psk, prologue, clientEphemeral);
        using var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, psk, prologue, serverEphemeral);
        var message1 = new byte[48];
        var message2 = new byte[48];
        Assert.True(initiator.WriteMessage1(message1));
        Assert.True(responder.ReadMessage1(message1));
        Assert.True(responder.WriteMessage2(message2));
        Assert.True(initiator.ReadMessage2(message2));
        initiator.Split(out var initiatorSend, out var initiatorReceive);
        responder.Split(out var responderSend, out var responderReceive);

        var reference = new ReferenceNoise(usePreSharedKey ? "Noise_NKpsk0_25519_ChaChaPoly_SHA256" : "Noise_NK_25519_ChaChaPoly_SHA256", prologue, serverPublic, psk);
        var expected1 = reference.InitiatorMessage1(clientEphemeral);
        var expected2 = reference.ResponderMessage2(serverEphemeral, serverPrivate, serverPublic);

        Assert.Equal(expected1, message1);
        Assert.Equal(expected2, message2);
        Assert.Equal(reference.Hash, initiator.HandshakeHash);
        Assert.Equal(reference.Hash, responder.HandshakeHash);
        Assert.Equal(reference.Key1, initiatorSend);
        Assert.Equal(reference.Key2, initiatorReceive);
        Assert.Equal(initiatorSend, responderReceive);
        Assert.Equal(initiatorReceive, responderSend);
    }

    [Fact]
    public void WrongServerKeyOrPreSharedKeyFailsMessageOne()
    {
        var serverPrivate = Fill(1);
        var serverPublic = X25519.GetPublicKey(serverPrivate);
        var otherPublic = X25519.GetPublicKey(Fill(2));
        var prologue = new byte[] { 1 };

        using (var initiator = NoiseHandshake.CreateInitiator(otherPublic, Array.Empty<byte>(), prologue))
        using (var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), prologue))
        {
            var message = new byte[48];
            Assert.True(initiator.WriteMessage1(message));
            Assert.False(responder.ReadMessage1(message));
        }

        using (var initiator = NoiseHandshake.CreateInitiator(serverPublic, Fill(5), prologue))
        using (var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Fill(6), prologue))
        {
            var message = new byte[48];
            Assert.True(initiator.WriteMessage1(message));
            Assert.False(responder.ReadMessage1(message));
        }

        using (var initiator = NoiseHandshake.CreateInitiator(serverPublic, Fill(5), prologue))
        using (var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), prologue))
        {
            var message = new byte[48];
            Assert.True(initiator.WriteMessage1(message));
            Assert.False(responder.ReadMessage1(message));
        }
    }

    [Fact]
    public void EveryModifiedByteOfMessagesIsRejected()
    {
        var serverPrivate = Fill(1);
        var serverPublic = X25519.GetPublicKey(serverPrivate);
        var prologue = new byte[] { 1 };

        using (var initiator = NoiseHandshake.CreateInitiator(serverPublic, Array.Empty<byte>(), prologue))
        {
            var message = new byte[48];
            Assert.True(initiator.WriteMessage1(message));

            for (var i = 0; i < message.Length; i++)
            {
                using var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), prologue);
                var copy = (byte[])message.Clone();
                copy[i] ^= 0x01;
                Assert.False(responder.ReadMessage1(copy), $"byte {i}");
            }
        }

        using (var initiator = NoiseHandshake.CreateInitiator(serverPublic, Array.Empty<byte>(), prologue))
        using (var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), prologue))
        {
            var message1 = new byte[48];
            var message2 = new byte[48];
            Assert.True(initiator.WriteMessage1(message1));
            Assert.True(responder.ReadMessage1(message1));
            Assert.True(responder.WriteMessage2(message2));

            for (var i = 0; i < message2.Length; i++)
            {
                using var trial = initiator.Clone();
                var copy = (byte[])message2.Clone();
                copy[i] ^= 0x01;
                Assert.False(trial.ReadMessage2(copy), $"byte {i}");
            }

            Assert.True(initiator.ReadMessage2(message2));
        }
    }

    [Fact]
    public void AllZeroSharedSecretIsRejected()
    {
        var serverPrivate = Fill(1);
        var serverPublic = X25519.GetPublicKey(serverPrivate);
        using var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), new byte[] { 1 });
        var message = new byte[48];
        Assert.False(responder.ReadMessage1(message));
    }

    [Fact]
    public void PrologueMismatchFails()
    {
        var serverPrivate = Fill(1);
        var serverPublic = X25519.GetPublicKey(serverPrivate);
        using var initiator = NoiseHandshake.CreateInitiator(serverPublic, Array.Empty<byte>(), new byte[] { 1 });
        using var responder = NoiseHandshake.CreateResponder(serverPrivate, serverPublic, Array.Empty<byte>(), new byte[] { 2 });
        var message = new byte[48];
        Assert.True(initiator.WriteMessage1(message));
        Assert.False(responder.ReadMessage1(message));
    }

    private sealed class ReferenceNoise
    {
        private readonly bool _psk;
        private readonly byte[] _pskValue;
        private readonly byte[] _responderStatic;
        private byte[] _h;
        private byte[] _ck;
        private byte[] _k = Array.Empty<byte>();
        private ulong _n;
        private byte[] _initiatorEphemeralPublic = Array.Empty<byte>();

        public ReferenceNoise(string name, byte[] prologue, byte[] responderStatic, byte[] psk)
        {
            _psk = psk.Length != 0;
            _pskValue = psk;
            _responderStatic = responderStatic;
            var nameBytes = Encoding.ASCII.GetBytes(name);
            _h = nameBytes.Length <= 32 ? nameBytes.Concat(new byte[32 - nameBytes.Length]).ToArray() : SHA256.HashData(nameBytes);
            _ck = _h;
            MixHash(prologue);
            MixHash(responderStatic);
        }

        public byte[] Hash => _h;

        public byte[] Key1 { get; private set; } = Array.Empty<byte>();

        public byte[] Key2 { get; private set; } = Array.Empty<byte>();

        public byte[] InitiatorMessage1(byte[] ephemeralPrivate)
        {
            var ephemeralPublic = PublicKey(ephemeralPrivate);
            _initiatorEphemeralPublic = ephemeralPublic;

            if (_psk)
            {
                MixKeyAndHash(_pskValue);
            }

            MixHash(ephemeralPublic);

            if (_psk)
            {
                MixKey(ephemeralPublic);
            }

            MixKey(Agree(ephemeralPrivate, _responderStatic));
            return ephemeralPublic.Concat(EncryptEmpty()).ToArray();
        }

        public byte[] ResponderMessage2(byte[] ephemeralPrivate, byte[] staticPrivate, byte[] staticPublic)
        {
            var ephemeralPublic = PublicKey(ephemeralPrivate);
            MixHash(ephemeralPublic);

            if (_psk)
            {
                MixKey(ephemeralPublic);
            }

            MixKey(Agree(ephemeralPrivate, _initiatorEphemeralPublic));
            var tag = EncryptEmpty();
            var split = Hkdf(_ck, Array.Empty<byte>(), 2);
            Key1 = split[0];
            Key2 = split[1];
            return ephemeralPublic.Concat(tag).ToArray();
        }

        private static byte[] PublicKey(byte[] privateKey)
        {
            var result = new byte[32];
            Org.BouncyCastle.Math.EC.Rfc7748.X25519.GeneratePublicKey(privateKey, 0, result, 0);
            return result;
        }

        private static byte[] Agree(byte[] privateKey, byte[] publicKey)
        {
            var result = new byte[32];
            Org.BouncyCastle.Math.EC.Rfc7748.X25519.CalculateAgreement(privateKey, 0, publicKey, 0, result, 0);
            return result;
        }

        private static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

        private static byte[][] Hkdf(byte[] chainingKey, byte[] input, int count)
        {
            var temporary = Hmac(chainingKey, input);
            var outputs = new List<byte[]>();
            var previous = Array.Empty<byte>();

            for (byte i = 1; i <= count; i++)
            {
                previous = Hmac(temporary, previous.Concat(new[] { i }).ToArray());
                outputs.Add(previous);
            }

            return outputs.ToArray();
        }

        private void MixHash(byte[] data) => _h = SHA256.HashData(_h.Concat(data).ToArray());

        private void MixKey(byte[] input)
        {
            var outputs = Hkdf(_ck, input, 2);
            _ck = outputs[0];
            _k = outputs[1];
            _n = 0;
        }

        private void MixKeyAndHash(byte[] input)
        {
            var outputs = Hkdf(_ck, input, 3);
            _ck = outputs[0];
            MixHash(outputs[1]);
            _k = outputs[2];
            _n = 0;
        }

        private byte[] EncryptEmpty()
        {
            var nonce = new byte[12];
            BitConverter.GetBytes(_n).CopyTo(nonce, 4);
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(true, new AeadParameters(new KeyParameter(_k), 128, nonce, _h));
            var output = new byte[16];
            cipher.DoFinal(output, 0);
            _n++;
            MixHash(output);
            return output;
        }
    }
}
