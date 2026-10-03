package lan.asta.freightcopilot

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SessionVault(context: Context) {
    private val preferences = context.getSharedPreferences("account_session", Context.MODE_PRIVATE)
    private val alias = "freight_copilot_session_key"

    fun save(serverUrl: String, session: AuthSession) {
        preferences.edit()
            .putString("server_url", serverUrl)
            .putString("display_name", session.account.displayName)
            .putString("email", session.account.email)
            .putString("access_token", encrypt(session.accessToken))
            .putString("refresh_token", encrypt(session.refreshToken))
            .apply()
    }

    fun serverUrl(): String = preferences.getString("server_url", "https://freightplanner.cipherdispatch.com")!!
    fun displayName(): String? = preferences.getString("display_name", null)
    fun email(): String? = preferences.getString("email", null)
    fun accessToken(): String? = preferences.getString("access_token", null)?.let(::decrypt)
    fun refreshToken(): String? = preferences.getString("refresh_token", null)?.let(::decrypt)
    fun savePairedDevice(id: String, name: String) = preferences.edit()
        .putString("paired_device_id", id)
        .putString("paired_device_name", name)
        .apply()
    fun pairedDeviceId(): String? = preferences.getString("paired_device_id", null)
    fun pairedDeviceName(): String? = preferences.getString("paired_device_name", null)
    fun clearPairedDevice() = preferences.edit().remove("paired_device_id").remove("paired_device_name").apply()
    fun checkedPlanItems(planIdentity: String): MutableSet<String> =
        preferences.getStringSet("checklist_$planIdentity", emptySet()).orEmpty().toMutableSet()
    fun saveCheckedPlanItems(planIdentity: String, items: Set<String>) =
        preferences.edit().putStringSet("checklist_$planIdentity", items.toSet()).apply()
    fun clear() = preferences.edit().clear().apply()

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build())
            generateKey()
        }
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv, Base64.NO_WRAP) + ":" + Base64.encodeToString(encrypted, Base64.NO_WRAP)
    }

    private fun decrypt(value: String): String? = runCatching {
        val parts = value.split(':', limit = 2)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)))
        String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), Charsets.UTF_8)
    }.getOrNull()
}

