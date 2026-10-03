package lan.asta.freightcopilot

import android.util.Log
import org.json.JSONObject
import java.net.URI
import java.net.URL
import javax.net.ssl.HttpsURLConnection

data class AccountIdentity(val id: String, val email: String, val displayName: String)
data class AuthSession(
    val accessToken: String,
    val refreshToken: String,
    val account: AccountIdentity
)
data class PairedDevice(val sessionId: String, val deviceName: String)
data class MobilePlan(
    val revision: Long,
    val title: String,
    val summary: String,
    val currentStopIndex: Int,
    val currentStopCompleted: Boolean,
    val stops: List<MobilePlanStop>
)
data class MobilePlanStop(
    val number: Int,
    val location: String,
    val travelMinutesFromPrevious: Int,
    val deliveries: List<String>,
    val pickups: List<String>,
    val packing: List<String>
)

class ApiClient(baseUrl: String) {
    private val base = baseUrl.trim().trimEnd('/')

    init {
        val uri = URI(base)
        require(uri.scheme.equals("https", ignoreCase = true)) { "The account server must use HTTPS." }
    }

    // Registration only starts email verification; the returned text tells the user what to do next.
    fun register(email: String, displayName: String, password: String): String =
        JSONObject(postRaw("/api/auth/register", JSONObject().apply {
            put("email", email)
            put("displayName", displayName)
            put("password", password)
        })).optString("message")

    fun resendVerification(email: String): String =
        JSONObject(postRaw("/api/auth/resend-verification", JSONObject().put("email", email))).optString("message")

    // Exchanges the single-use token from the Windows QR code for a signed-in, already paired session.
    fun linkWithQr(token: String): Pair<AuthSession, PairedDevice> {
        val json = JSONObject(postRaw("/api/auth/link", JSONObject().put("token", token)))
        return parseSession(json) to PairedDevice(json.getString("sessionId"), json.getString("deviceName"))
    }

    fun login(email: String, password: String): AuthSession =
        post("/api/auth/login", JSONObject().apply {
            put("email", email)
            put("password", password)
        })

    fun refresh(refreshToken: String): AuthSession =
        post("/api/auth/refresh", JSONObject().put("refreshToken", refreshToken))

    fun pairPc(accessToken: String, code: String): PairedDevice {
        val json = JSONObject(postRaw("/api/pairing/mobile", JSONObject().put("code", code), accessToken))
        return PairedDevice(json.getString("sessionId"), json.getString("deviceName"))
    }

    fun sendChecklistUpdate(accessToken: String, sessionId: String, label: String, payload: String) {
        postRaw("/api/pairing/$sessionId/commands", JSONObject().apply {
            put("label", label.take(80))
            put("shortcut", payload.take(64))
        }, accessToken)
    }

    fun getPlan(accessToken: String, sessionId: String): MobilePlan? {
        val connection = URL(base + "/api/pairing/$sessionId/plan").openConnection() as HttpsURLConnection
        connection.requestMethod = "GET"
        connection.connectTimeout = 15_000
        connection.readTimeout = 20_000
        connection.setRequestProperty("Authorization", "Bearer $accessToken")
        val responseCode = connection.responseCode
        Log.i("FreightCopilot", "Plan poll returned HTTP $responseCode for session $sessionId")
        if (responseCode == 204) {
            connection.disconnect()
            return null
        }
        val success = responseCode in 200..299
        val text = (if (success) connection.inputStream else connection.errorStream)?.bufferedReader()?.use { it.readText() }.orEmpty()
        connection.disconnect()
        if (!success) throw ApiException(errorMessage(text, responseCode), responseCode)
        val json = JSONObject(text)
        val stopsJson = json.getJSONArray("stops")
        return MobilePlan(
            json.getLong("revision"), json.getString("title"), json.getString("summary"),
            json.optInt("currentStopIndex", 0), json.optBoolean("currentStopCompleted", false),
            List(stopsJson.length()) { index ->
                val stop = stopsJson.getJSONObject(index)
                MobilePlanStop(
                    stop.getInt("number"), stop.getString("location"), stop.optInt("travelMinutesFromPrevious", 0),
                    stop.optionalStrings("deliveries"), stop.optionalStrings("pickups"),
                    stop.optionalStrings("packing")
                )
            }
        )
    }

    fun logout(refreshToken: String) {
        postRaw("/api/auth/logout", JSONObject().put("refreshToken", refreshToken))
    }

    private fun post(path: String, body: JSONObject): AuthSession = parseSession(JSONObject(postRaw(path, body)))

    private fun parseSession(json: JSONObject): AuthSession {
        val account = json.getJSONObject("account")
        return AuthSession(
            json.getString("accessToken"),
            json.getString("refreshToken"),
            AccountIdentity(account.getString("id"), account.getString("email"), account.getString("displayName"))
        )
    }

    private fun postRaw(path: String, body: JSONObject, accessToken: String? = null): String {
        val connection = URL(base + path).openConnection() as HttpsURLConnection
        connection.requestMethod = "POST"
        connection.connectTimeout = 15_000
        connection.readTimeout = 20_000
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "application/json")
        if (!accessToken.isNullOrBlank()) connection.setRequestProperty("Authorization", "Bearer $accessToken")
        connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        val responseCode = connection.responseCode
        val success = responseCode in 200..299
        val text = (if (success) connection.inputStream else connection.errorStream)?.bufferedReader()?.use { it.readText() }.orEmpty()
        connection.disconnect()
        if (!success) {
            throw ApiException(errorMessage(text, responseCode), responseCode)
        }
        return text
    }

    private fun errorMessage(text: String, responseCode: Int): String = runCatching {
        val problem = JSONObject(text)
        problem.optString("detail").ifBlank { problem.optString("error") }.ifBlank { problem.optString("title") }
    }.getOrDefault("").ifBlank { "The server returned HTTP $responseCode." }
}

class ApiException(message: String, val statusCode: Int = 0) : Exception(message)

private fun org.json.JSONArray.strings(): List<String> = List(length()) { index -> getString(index) }
private fun JSONObject.optionalStrings(name: String): List<String> = optJSONArray(name)?.strings().orEmpty()

