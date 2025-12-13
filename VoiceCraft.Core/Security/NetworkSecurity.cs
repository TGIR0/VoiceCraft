using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VoiceCraft.Core.Security
{
    public class NetworkSecurity : IDisposable
    {
        private const int NoncePrefixSize = 4;
        private const int AesKeySize = 32;
        private const int ReplayWindowSize = 64;

        private static readonly byte[] HandshakeTranscriptLabel =
            Encoding.UTF8.GetBytes("VoiceCraft.NetworkSecurity.Handshake.v1");

        private static readonly byte[] KeyScheduleInfo =
            Encoding.UTF8.GetBytes("VoiceCraft.NetworkSecurity.KeySchedule.v1");

        private ECDiffieHellman? _ecdh;
        private byte[]? _sendKey;
        private byte[]? _receiveKey;
        private byte[]? _sendNoncePrefix;
        private byte[]? _receiveNoncePrefix;

        private long _sendCounter;
        private ulong _receiveMaxCounter;
        private ulong _receiveWindow;
        private readonly object _receiveSync = new object();
        
        // AES-GCM constants
        public const int NonceSize = 12; // 96 bits
        public const int TagSize = 16;   // 128 bits

        public byte[] PublicKey { get; private set; } = Array.Empty<byte>();
        public bool IsHandshakeComplete => _sendKey != null && _receiveKey != null;

        public NetworkSecurity()
        {
            Initialize();
        }

        private void Initialize()
        {
            _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            // Export public key as raw EC point (X + Y coordinates)
            var parameters = _ecdh.ExportParameters(false);
            PublicKey = new byte[parameters.Q.X!.Length + parameters.Q.Y!.Length];
            Buffer.BlockCopy(parameters.Q.X, 0, PublicKey, 0, parameters.Q.X.Length);
            Buffer.BlockCopy(parameters.Q.Y, 0, PublicKey, parameters.Q.X.Length, parameters.Q.Y.Length);
        }

        public void CompleteHandshake(byte[] remotePublicKey)
        {
            if (_ecdh == null) throw new ObjectDisposedException(nameof(NetworkSecurity));

            if (remotePublicKey == null) throw new ArgumentNullException(nameof(remotePublicKey));
            if (remotePublicKey.Length == 0 || (remotePublicKey.Length % 2) != 0)
                throw new CryptographicException("Invalid remote public key");
            if (PublicKey.Length == 0) throw new CryptographicException("Local public key not initialized");
            if (remotePublicKey.Length != PublicKey.Length) throw new CryptographicException("Invalid remote public key");

            ClearKeyMaterial();

            // Import public key from raw EC point
            var keySize = remotePublicKey.Length / 2;
            var x = new byte[keySize];
            var y = new byte[keySize];
            Buffer.BlockCopy(remotePublicKey, 0, x, 0, keySize);
            Buffer.BlockCopy(remotePublicKey, keySize, y, 0, keySize);

            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            };

            using var peerEcdh = ECDiffieHellman.Create(parameters);
            var sharedSecret = _ecdh.DeriveKeyMaterial(peerEcdh.PublicKey);
            try
            {
                var transcriptHash = ComputeHandshakeTranscriptHash(PublicKey, remotePublicKey);

                var prk = HkdfExtract(transcriptHash, sharedSecret);
                Array.Clear(transcriptHash, 0, transcriptHash.Length);

                var okm = HkdfExpand(prk, KeyScheduleInfo, (AesKeySize * 2) + (NoncePrefixSize * 2));
                Array.Clear(prk, 0, prk.Length);

                var key0 = new byte[AesKeySize];
                var key1 = new byte[AesKeySize];
                var nonce0 = new byte[NoncePrefixSize];
                var nonce1 = new byte[NoncePrefixSize];
                Buffer.BlockCopy(okm, 0, key0, 0, AesKeySize);
                Buffer.BlockCopy(okm, AesKeySize, key1, 0, AesKeySize);
                Buffer.BlockCopy(okm, AesKeySize * 2, nonce0, 0, NoncePrefixSize);
                Buffer.BlockCopy(okm, (AesKeySize * 2) + NoncePrefixSize, nonce1, 0, NoncePrefixSize);
                Array.Clear(okm, 0, okm.Length);

                var localIsLower = LexicographicCompare(PublicKey, remotePublicKey) < 0;
                if (localIsLower)
                {
                    _sendKey = key0;
                    _receiveKey = key1;
                    _sendNoncePrefix = nonce0;
                    _receiveNoncePrefix = nonce1;
                }
                else
                {
                    _sendKey = key1;
                    _receiveKey = key0;
                    _sendNoncePrefix = nonce1;
                    _receiveNoncePrefix = nonce0;
                }

                Interlocked.Exchange(ref _sendCounter, 0);
                lock (_receiveSync)
                {
                    _receiveMaxCounter = 0;
                    _receiveWindow = 0;
                }
            }
            finally
            {
                Array.Clear(sharedSecret, 0, sharedSecret.Length);
            }

            _ecdh.Dispose();
            _ecdh = null;
        }

        public (byte[] encryptedData, byte[] iv, byte[] tag) Encrypt(byte[] data)
        {
            if (_sendKey == null || _sendNoncePrefix == null) throw new InvalidOperationException("Handshake not complete");

            var iv = new byte[NonceSize];
            Buffer.BlockCopy(_sendNoncePrefix, 0, iv, 0, NoncePrefixSize);

            var counter = unchecked((ulong)Interlocked.Increment(ref _sendCounter));
            BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(NoncePrefixSize, 8), counter);

            var tag = new byte[TagSize];
            var encryptedData = new byte[data.Length];

            using var aes = new AesGcm(_sendKey);
            aes.Encrypt(iv, data, encryptedData, tag);

            return (encryptedData, iv, tag);
        }

        public byte[] Decrypt(byte[] encryptedData, byte[] iv, byte[] tag)
        {
            if (_receiveKey == null || _receiveNoncePrefix == null) throw new InvalidOperationException("Handshake not complete");

            if (encryptedData == null) throw new ArgumentNullException(nameof(encryptedData));
            if (iv == null || iv.Length != NonceSize) throw new CryptographicException("Invalid nonce");
            if (tag == null || tag.Length != TagSize) throw new CryptographicException("Invalid tag");
            if (!iv.AsSpan(0, NoncePrefixSize).SequenceEqual(_receiveNoncePrefix))
                throw new CryptographicException("Invalid nonce");

            var counter = BinaryPrimitives.ReadUInt64BigEndian(iv.AsSpan(NoncePrefixSize, 8));

            var decryptedData = new byte[encryptedData.Length];

            lock (_receiveSync)
            {
                if (IsReplayLocked(counter)) throw new CryptographicException("Replay detected");

                using var aes = new AesGcm(_receiveKey);
                aes.Decrypt(iv, encryptedData, tag, decryptedData);

                CommitCounterLocked(counter);
            }

            return decryptedData;
        }

        private bool IsReplayLocked(ulong counter)
        {
            if (counter <= _receiveMaxCounter)
            {
                var diff = _receiveMaxCounter - counter;
                if (diff >= ReplayWindowSize) return true;
                var mask = 1UL << (int)diff;
                return (_receiveWindow & mask) != 0;
            }

            return false;
        }

        private void CommitCounterLocked(ulong counter)
        {
            if (counter > _receiveMaxCounter)
            {
                var shift = counter - _receiveMaxCounter;
                if (shift >= ReplayWindowSize)
                {
                    _receiveWindow = 1;
                }
                else
                {
                    _receiveWindow = (_receiveWindow << (int)shift) | 1;
                }

                _receiveMaxCounter = counter;
                return;
            }

            var diff = _receiveMaxCounter - counter;
            if (diff >= ReplayWindowSize) return;
            _receiveWindow |= 1UL << (int)diff;
        }

        private static byte[] ComputeHandshakeTranscriptHash(byte[] localPublicKey, byte[] remotePublicKey)
        {
            var localIsLower = LexicographicCompare(localPublicKey, remotePublicKey) < 0;
            var first = localIsLower ? localPublicKey : remotePublicKey;
            var second = localIsLower ? remotePublicKey : localPublicKey;

            var transcript = new byte[HandshakeTranscriptLabel.Length + first.Length + second.Length];
            Buffer.BlockCopy(HandshakeTranscriptLabel, 0, transcript, 0, HandshakeTranscriptLabel.Length);
            Buffer.BlockCopy(first, 0, transcript, HandshakeTranscriptLabel.Length, first.Length);
            Buffer.BlockCopy(second, 0, transcript, HandshakeTranscriptLabel.Length + first.Length, second.Length);

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(transcript);
            Array.Clear(transcript, 0, transcript.Length);
            return hash;
        }

        private static int LexicographicCompare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var length = Math.Min(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = a[i] - b[i];
                if (diff != 0) return diff;
            }
            return a.Length - b.Length;
        }

        private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
        {
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (ikm == null) throw new ArgumentNullException(nameof(ikm));
            using var hmac = new HMACSHA256(salt);
            return hmac.ComputeHash(ikm);
        }

        private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
        {
            if (prk == null) throw new ArgumentNullException(nameof(prk));
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

            const int hashLen = 32;
            var n = (int)Math.Ceiling(length / (double)hashLen);
            if (n > 255) throw new ArgumentOutOfRangeException(nameof(length));

            var okm = new byte[length];
            var t = Array.Empty<byte>();
            var pos = 0;

            using var hmac = new HMACSHA256(prk);
            for (var i = 1; i <= n; i++)
            {
                var input = new byte[t.Length + info.Length + 1];
                if (t.Length > 0) Buffer.BlockCopy(t, 0, input, 0, t.Length);
                Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
                input[input.Length - 1] = (byte)i;

                var newT = hmac.ComputeHash(input);
                Array.Clear(input, 0, input.Length);

                if (t.Length > 0) Array.Clear(t, 0, t.Length);
                t = newT;

                var toCopy = Math.Min(hashLen, length - pos);
                Buffer.BlockCopy(t, 0, okm, pos, toCopy);
                pos += toCopy;
            }

            if (t.Length > 0) Array.Clear(t, 0, t.Length);
            return okm;
        }

        private void ClearKeyMaterial()
        {
            if (_sendKey != null)
            {
                Array.Clear(_sendKey, 0, _sendKey.Length);
                _sendKey = null;
            }

            if (_sendNoncePrefix != null)
            {
                Array.Clear(_sendNoncePrefix, 0, _sendNoncePrefix.Length);
                _sendNoncePrefix = null;
            }

            lock (_receiveSync)
            {
                if (_receiveKey != null)
                {
                    Array.Clear(_receiveKey, 0, _receiveKey.Length);
                    _receiveKey = null;
                }

                if (_receiveNoncePrefix != null)
                {
                    Array.Clear(_receiveNoncePrefix, 0, _receiveNoncePrefix.Length);
                    _receiveNoncePrefix = null;
                }

                _receiveMaxCounter = 0;
                _receiveWindow = 0;
            }

            Interlocked.Exchange(ref _sendCounter, 0);
        }

        public void Dispose()
        {
            _ecdh?.Dispose();
            _ecdh = null;
            ClearKeyMaterial();
            GC.SuppressFinalize(this);
        }
    }
}
