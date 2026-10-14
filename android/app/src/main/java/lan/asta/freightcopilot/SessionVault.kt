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
    fun shareAnonymousUsage(): Boolean = preferences.getBoolean("share_anonymous_usage", false)
    fun setShareAnonymousUsage(value: Boolean) = preferences.edit().putBoolean("share_anonymous_usage", value).apply()
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
    // When each step of a trade run was ticked, by step id, in epoch milliseconds.
    fun stepTimes(runIdentity: String): MutableMap<String, Long> =
        preferences.getStringSet("steps_$runIdentity", emptySet()).orEmpty().mapNotNull { entry ->
            val at = entry.lastIndexOf('=')
            val time = entry.substring(at + 1).toLongOrNull()
            if (at <= 0 || time == null) null else entry.substring(0, at) to time
        }.toMap().toMutableMap()
    fun saveStepTimes(runIdentity: String, times: Map<String, Long>) =
        preferences.edit().putStringSet("steps_$runIdentity", times.map { "${it.key}=${it.value}" }.toSet()).apply()
    // The moment this tablet first showed the run: its clock starts there and survives a restart of the app.
    fun runStart(runIdentity: String): Long {
        val known = preferences.getLong("start_$runIdentity", 0L)
        if (known > 0L) return known
        val now = System.currentTimeMillis()
        preferences.edit().putLong("start_$runIdentity", now).apply()
        return now
    }
    fun forgetRun(runIdentity: String) = preferences.edit().remove("steps_$runIdentity").remove("start_$runIdentity").apply()
    fun clear() {
        val shareUsage = shareAnonymousUsage()
        preferences.edit().clear().putBoolean("share_anonymous_usage", shareUsage).apply()
    }

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

