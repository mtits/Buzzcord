package com.marcturtal.buzzcord.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import com.marcturtal.buzzcord.net.TAG
import com.marcturtal.buzzcord.net.bool
import com.marcturtal.buzzcord.net.jobj
import com.marcturtal.buzzcord.net.objs
import com.marcturtal.buzzcord.net.str
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

// Signed-in accounts. Tokens are encrypted with a key that lives in the Android Keystore (it
// never leaves the device) and the file sits in no-backup storage.
class AccountStore(ctx: Context) {
    class Saved(val id: String, val token: String, val name: String?, val avatar: String?, val paused: Boolean)

    private val file = File(ctx.noBackupFilesDir, "accounts.json")

    fun load(): List<Saved> {
        if (!file.exists()) return emptyList()
        return try {
            val root = JSONObject(file.readText())
            root.objs("accounts").mapNotNull { a ->
                val token = try { decrypt(a.str("token") ?: return@mapNotNull null) } catch (e: Exception) {
                    Log.w(TAG, "account ${a.str("id")}: can't decrypt token (${e.message})")
                    return@mapNotNull null
                }
                Saved(a.str("id") ?: Session.idFromToken(token), token, a.str("name"), a.str("avatar"), a.bool("paused"))
            }
        } catch (e: Exception) {
            Log.w(TAG, "accounts: ${e.message}")
            emptyList()
        }
    }

    fun save(list: List<Saved>) {
        val arr = JSONArray()
        for (s in list) arr.put(jobj("id" to s.id, "token" to encrypt(s.token), "name" to s.name, "avatar" to s.avatar, "paused" to s.paused))
        val tmp = File(file.parentFile, "accounts.json.tmp")
        tmp.writeText(jobj("accounts" to arr).toString())
        tmp.renameTo(file)
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return gen.generateKey()
    }

    private fun encrypt(text: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        return Base64.encodeToString(c.iv + c.doFinal(text.toByteArray()), Base64.NO_WRAP)
    }

    private fun decrypt(b64: String): String {
        val data = Base64.decode(b64, Base64.NO_WRAP)
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data, 0, 12))
        return String(c.doFinal(data, 12, data.size - 12))
    }

    companion object {
        private const val ALIAS = "buzzcord_tokens"
    }
}
