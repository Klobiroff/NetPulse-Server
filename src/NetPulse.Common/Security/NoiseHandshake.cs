using System;
using System.Buffers.Binary;
using System.Text;

namespace NetPulse.Common.Security
{
    public sealed class NoiseHandshake : IDisposable
    {
        public const string ProtocolName = "Noise_NK_25519_ChaChaPoly_SHA256";
        public const string ProtocolNamePsk = "Noise_NKpsk0_25519_ChaChaPoly_SHA256";
        public const int MessageSize = SecureRecord.NoiseMessageSize;
        public const int PreSharedKeySize = 32;
        public const int MaxPrologueSize = 64;

        private const int HashSize = 32;

        private readonly bool _initiator;
        private readonly bool _hasPreSharedKey;
        private readonly byte[] _preSharedKey;
        private readonly byte[] _staticPublic;
        private readonly byte[] _staticPrivate;
        private readonly byte[] _ephemeralPrivate;
        private readonly byte[] _ephemeralPublic;
        private readonly byte[] _hash = new byte[HashSize];
        private readonly byte[] _chainingKey = new byte[HashSize];
        private readonly byte[] _cipherKey = new byte[HashSize];
        private readonly byte[] _remoteEphemeral = new byte[HashSize];
        private ulong _nonce;
        private bool _completed;
        private bool _failed;

        private NoiseHandshake(
            bool initiator,
            ReadOnlySpan<byte> staticPublic,
            ReadOnlySpan<byte> staticPrivate,
            ReadOnlySpan<byte> preSharedKey,
            ReadOnlySpan<byte> prologue,
            ReadOnlySpan<byte> ephemeralPrivate)
        {
            if (prologue.Length > MaxPrologueSize)
            {
                throw new ArgumentException("Prologue is too long", nameof(prologue));
            }

            if (preSharedKey.Length != 0 && preSharedKey.Length != PreSharedKeySize)
            {
                throw new ArgumentException("Pre-shared key must be 32 bytes", nameof(preSharedKey));
            }

            _initiator = initiator;
            _staticPublic = staticPublic.ToArray();
            _staticPrivate = staticPrivate.ToArray();
            _hasPreSharedKey = preSharedKey.Length != 0;
            _preSharedKey = preSharedKey.ToArray();
            _ephemeralPrivate = ephemeralPrivate.Length == 0 ? X25519.GeneratePrivateKey() : ephemeralPrivate.ToArray();
            _ephemeralPublic = X25519.GetPublicKey(_ephemeralPrivate);

            var name = Encoding.ASCII.GetBytes(_hasPreSharedKey ? ProtocolNamePsk : ProtocolName);

            if (name.Length <= HashSize)
            {
                name.CopyTo(_hash, 0);
            }
            else
            {
                HmacSha256.Hash(name, _hash);
            }

            Buffer.BlockCopy(_hash, 0, _chainingKey, 0, HashSize);
            MixHash(prologue);
            MixHash(_staticPublic);
        }

        private NoiseHandshake(NoiseHandshake other)
        {
            _initiator = other._initiator;
            _hasPreSharedKey = other._hasPreSharedKey;
            _preSharedKey = (byte[])other._preSharedKey.Clone();
            _staticPublic = (byte[])other._staticPublic.Clone();
            _staticPrivate = (byte[])other._staticPrivate.Clone();
            _ephemeralPrivate = (byte[])other._ephemeralPrivate.Clone();
            _ephemeralPublic = (byte[])other._ephemeralPublic.Clone();
            _hash = (byte[])other._hash.Clone();
            _chainingKey = (byte[])other._chainingKey.Clone();
            _cipherKey = (byte[])other._cipherKey.Clone();
            _remoteEphemeral = (byte[])other._remoteEphemeral.Clone();
            _nonce = other._nonce;
            _completed = other._completed;
            _failed = other._failed;
        }

        public byte[] HandshakeHash => (byte[])_hash.Clone();

        public byte[] LocalEphemeralPublic => (byte[])_ephemeralPublic.Clone();

        public bool IsCompleted => _completed;

        public static NoiseHandshake CreateInitiator(
            ReadOnlySpan<byte> serverPublicKey,
            ReadOnlySpan<byte> preSharedKey,
            ReadOnlySpan<byte> prologue,
            ReadOnlySpan<byte> ephemeralPrivate = default)
        {
            if (serverPublicKey.Length != X25519.KeySize)
            {
                throw new ArgumentException("Server public key must be 32 bytes", nameof(serverPublicKey));
            }

            return new NoiseHandshake(true, serverPublicKey, ReadOnlySpan<byte>.Empty, preSharedKey, prologue, ephemeralPrivate);
        }

        public static NoiseHandshake CreateResponder(
            ReadOnlySpan<byte> staticPrivateKey,
            ReadOnlySpan<byte> staticPublicKey,
            ReadOnlySpan<byte> preSharedKey,
            ReadOnlySpan<byte> prologue,
            ReadOnlySpan<byte> ephemeralPrivate = default)
        {
            if (staticPrivateKey.Length != X25519.KeySize || staticPublicKey.Length != X25519.KeySize)
            {
                throw new ArgumentException("Static keys must be 32 bytes");
            }

            return new NoiseHandshake(false, staticPublicKey, staticPrivateKey, preSharedKey, prologue, ephemeralPrivate);
        }

        public bool WriteMessage1(Span<byte> output)
        {
            if (!_initiator || _failed || _completed || output.Length < MessageSize)
            {
                return false;
            }

            if (_hasPreSharedKey)
            {
                MixKeyAndHash(_preSharedKey);
            }

            _ephemeralPublic.CopyTo(output);
            MixHash(_ephemeralPublic);

            if (_hasPreSharedKey)
            {
                MixKey(_ephemeralPublic);
            }

            if (!MixDiffieHellman(_ephemeralPrivate, _staticPublic))
            {
                return Fail();
            }

            EncryptEmpty(output.Slice(HashSize, ChaCha20Poly1305.TagSize));
            return true;
        }

        public bool ReadMessage1(ReadOnlySpan<byte> input)
        {
            if (_initiator || _failed || _completed || input.Length < MessageSize)
            {
                return false;
            }

            if (_hasPreSharedKey)
            {
                MixKeyAndHash(_preSharedKey);
            }

            input.Slice(0, HashSize).CopyTo(_remoteEphemeral);
            MixHash(_remoteEphemeral);

            if (_hasPreSharedKey)
            {
                MixKey(_remoteEphemeral);
            }

            if (!MixDiffieHellman(_staticPrivate, _remoteEphemeral))
            {
                return Fail();
            }

            return DecryptEmpty(input.Slice(HashSize, ChaCha20Poly1305.TagSize)) || Fail();
        }

        public bool WriteMessage2(Span<byte> output)
        {
            if (_initiator || _failed || _completed || output.Length < MessageSize)
            {
                return false;
            }

            _ephemeralPublic.CopyTo(output);
            MixHash(_ephemeralPublic);

            if (_hasPreSharedKey)
            {
                MixKey(_ephemeralPublic);
            }

            if (!MixDiffieHellman(_ephemeralPrivate, _remoteEphemeral))
            {
                return Fail();
            }

            EncryptEmpty(output.Slice(HashSize, ChaCha20Poly1305.TagSize));
            _completed = true;
            return true;
        }

        public bool ReadMessage2(ReadOnlySpan<byte> input)
        {
            if (!_initiator || _failed || _completed || input.Length < MessageSize)
            {
                return false;
            }

            input.Slice(0, HashSize).CopyTo(_remoteEphemeral);
            MixHash(_remoteEphemeral);

            if (_hasPreSharedKey)
            {
                MixKey(_remoteEphemeral);
            }

            if (!MixDiffieHellman(_ephemeralPrivate, _remoteEphemeral))
            {
                return Fail();
            }

            if (!DecryptEmpty(input.Slice(HashSize, ChaCha20Poly1305.TagSize)))
            {
                return Fail();
            }

            _completed = true;
            return true;
        }

        public void Split(out byte[] sendKey, out byte[] receiveKey)
        {
            if (!_completed)
            {
                throw new InvalidOperationException("Handshake is not complete");
            }

            var first = new byte[HashSize];
            var second = new byte[HashSize];
            Hkdf.Derive(_chainingKey, ReadOnlySpan<byte>.Empty, first, second, Span<byte>.Empty);
            sendKey = _initiator ? first : second;
            receiveKey = _initiator ? second : first;
        }

        public NoiseHandshake Clone() => new NoiseHandshake(this);

        public void Dispose()
        {
            ConstantTime.Zero(_hash);
            ConstantTime.Zero(_chainingKey);
            ConstantTime.Zero(_cipherKey);
            ConstantTime.Zero(_preSharedKey);
            ConstantTime.Zero(_staticPrivate);
            ConstantTime.Zero(_ephemeralPrivate);
            ConstantTime.Zero(_remoteEphemeral);
            _failed = true;
        }

        private bool Fail()
        {
            _failed = true;
            return false;
        }

        private bool MixDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
        {
            Span<byte> shared = stackalloc byte[X25519.KeySize];

            if (!X25519.TryAgree(privateKey, publicKey, shared))
            {
                shared.Clear();
                return false;
            }

            MixKey(shared);
            shared.Clear();
            return true;
        }

        private void MixHash(ReadOnlySpan<byte> data)
        {
            Span<byte> input = stackalloc byte[HashSize + 128];
            _hash.CopyTo(input);
            data.CopyTo(input.Slice(HashSize));
            HmacSha256.Hash(input.Slice(0, HashSize + data.Length), _hash);
        }

        private void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
        {
            Span<byte> chaining = stackalloc byte[HashSize];
            Span<byte> key = stackalloc byte[HashSize];
            Hkdf.Derive(_chainingKey, inputKeyMaterial, chaining, key, Span<byte>.Empty);
            chaining.CopyTo(_chainingKey);
            key.CopyTo(_cipherKey);
            _nonce = 0;
            chaining.Clear();
            key.Clear();
        }

        private void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
        {
            Span<byte> chaining = stackalloc byte[HashSize];
            Span<byte> temporaryHash = stackalloc byte[HashSize];
            Span<byte> key = stackalloc byte[HashSize];
            Hkdf.Derive(_chainingKey, inputKeyMaterial, chaining, temporaryHash, key);
            chaining.CopyTo(_chainingKey);
            MixHash(temporaryHash);
            key.CopyTo(_cipherKey);
            _nonce = 0;
            chaining.Clear();
            temporaryHash.Clear();
            key.Clear();
        }

        private void EncryptEmpty(Span<byte> tag)
        {
            Span<byte> nonce = stackalloc byte[ChaCha20Poly1305.NonceSize];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), _nonce);
            ChaCha20Poly1305.Encrypt(_cipherKey, nonce, _hash, Span<byte>.Empty, tag);
            _nonce++;
            MixHash(tag);
        }

        private bool DecryptEmpty(ReadOnlySpan<byte> tag)
        {
            Span<byte> nonce = stackalloc byte[ChaCha20Poly1305.NonceSize];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4, 8), _nonce);

            if (!ChaCha20Poly1305.Decrypt(_cipherKey, nonce, _hash, Span<byte>.Empty, tag))
            {
                return false;
            }

            _nonce++;
            MixHash(tag);
            return true;
        }
    }
}
