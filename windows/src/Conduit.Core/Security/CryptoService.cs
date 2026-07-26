using System.Security.Cryptography;

namespace Conduit.Core.Security;

/// <summary>
/// Handles the per-device key pair and per-session symmetric encryption.
///
/// Key exchange:  ECDH over NIST P-256 (built into .NET and Android's java.security).
/// Session cipher: AES-256-GCM. The AES key = SHA-256(ECDH shared secret).
///
/// Public keys are exchanged as base64 SubjectPublicKeyInfo (X.509) DER, which is the
/// same encoding Android produces via X509EncodedKeySpec — so both sides interop.
/// </summary>
public sealed class CryptoService
{
    private readonly ECDiffieHellman _ecdh;

    public CryptoService()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>Load a persisted key pair from a base64 PKCS#8 private key, or generate a fresh one.</summary>
    public static CryptoService LoadOrCreate(string? privateKeyB64, out string privateKeyExport)
    {
        var svc = new CryptoService();
        if (!string.IsNullOrEmpty(privateKeyB64))
        {
            try
            {
                svc._ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
            }
            catch
            {
                // fall through to fresh key
            }
        }
        privateKeyExport = Convert.ToBase64String(svc._ecdh.ExportPkcs8PrivateKey());
        return svc;
    }

    /// <summary>Our public key, base64 SubjectPublicKeyInfo, to send in the identity packet.</summary>
    public string PublicKeyBase64 => Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo());

    /// <summary>Derive the AES-256 session key from a peer's base64 public key.</summary>
    public byte[] DeriveSessionKey(string peerPublicKeyB64)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKeyB64), out _);
        byte[] shared = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        return SHA256.HashData(shared);
    }
}

/// <summary>AES-256-GCM framing used for every post-handshake payload: nonce(12) || ct || tag(16).</summary>
public sealed class SessionCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SessionCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes", nameof(key));
        _key = key;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ct = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plaintext, ct, tag);

        var output = new byte[NonceSize + ct.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(ct, 0, output, NonceSize, ct.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize + ct.Length, TagSize);
        return output;
    }

    public byte[] Decrypt(byte[] frame)
    {
        if (frame.Length < NonceSize + TagSize)
            throw new CryptographicException("Frame too short");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        int ctLen = frame.Length - NonceSize - TagSize;
        var ct = new byte[ctLen];

        Buffer.BlockCopy(frame, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(frame, NonceSize, ct, 0, ctLen);
        Buffer.BlockCopy(frame, NonceSize + ctLen, tag, 0, TagSize);

        var plaintext = new byte[ctLen];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(nonce, ct, tag, plaintext);
        return plaintext;
    }
}
