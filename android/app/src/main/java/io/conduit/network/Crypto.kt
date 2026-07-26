package io.conduit.network

import android.util.Base64
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Per-device key pair (ECDH P-256) and session key derivation. Interops with the Windows
 * side: public keys are base64 X.509 SubjectPublicKeyInfo, private keys base64 PKCS#8,
 * session key = SHA-256(ECDH shared secret). See PROTOCOL.md §5.
 */
class CryptoService private constructor(private val keyPair: KeyPair) {

    val publicKeyBase64: String
        get() = Base64.encodeToString(keyPair.public.encoded, Base64.NO_WRAP)

    val privateKeyBase64: String
        get() = Base64.encodeToString(keyPair.private.encoded, Base64.NO_WRAP)

    fun deriveSessionKey(peerPublicKeyB64: String): ByteArray {
        val peerBytes = Base64.decode(peerPublicKeyB64, Base64.NO_WRAP)
        val peerKey = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(peerBytes))
        val agreement = KeyAgreement.getInstance("ECDH").apply {
            init(keyPair.private)
            doPhase(peerKey, true)
        }
        val shared = agreement.generateSecret()
        return MessageDigest.getInstance("SHA-256").digest(shared)
    }

    companion object {
        /** Load a stable identity from persisted key pair, or generate a fresh one. */
        fun loadOrCreate(privateKeyB64: String?, publicKeyB64: String?): CryptoService {
            if (!privateKeyB64.isNullOrEmpty() && !publicKeyB64.isNullOrEmpty()) {
                try {
                    val kf = KeyFactory.getInstance("EC")
                    val priv = kf.generatePrivate(PKCS8EncodedKeySpec(Base64.decode(privateKeyB64, Base64.NO_WRAP)))
                    val pub = kf.generatePublic(X509EncodedKeySpec(Base64.decode(publicKeyB64, Base64.NO_WRAP)))
                    return CryptoService(KeyPair(pub, priv))
                } catch (_: Exception) {
                    // fall through to fresh key
                }
            }
            val gen = KeyPairGenerator.getInstance("EC").apply {
                initialize(ECGenParameterSpec("secp256r1"))
            }
            return CryptoService(gen.generateKeyPair())
        }
    }
}

/** AES-256-GCM framing: nonce(12) || ciphertext || tag(16). Matches the .NET SessionCipher. */
class SessionCipher(key: ByteArray) {
    private val keySpec = SecretKeySpec(key, "AES")
    private val random = SecureRandom()

    fun encrypt(plaintext: ByteArray): ByteArray {
        val nonce = ByteArray(NONCE).also { random.nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.ENCRYPT_MODE, keySpec, GCMParameterSpec(TAG_BITS, nonce))
        }
        val ctWithTag = cipher.doFinal(plaintext) // ciphertext || tag
        return nonce + ctWithTag
    }

    fun decrypt(frame: ByteArray): ByteArray {
        require(frame.size >= NONCE + TAG) { "Frame too short" }
        val nonce = frame.copyOfRange(0, NONCE)
        val ctWithTag = frame.copyOfRange(NONCE, frame.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, keySpec, GCMParameterSpec(TAG_BITS, nonce))
        }
        return cipher.doFinal(ctWithTag)
    }

    private companion object {
        const val NONCE = 12
        const val TAG = 16
        const val TAG_BITS = 128
    }
}
