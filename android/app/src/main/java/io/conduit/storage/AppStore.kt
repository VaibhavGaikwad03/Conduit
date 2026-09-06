package io.conduit.storage

import android.content.Context
import android.os.Build
import io.conduit.model.DeviceType
import io.conduit.model.PairedDevice
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * Persisted identity, settings, and the trusted-device list, backed by SharedPreferences.
 */
class AppStore(context: Context) {
    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences("conduit", Context.MODE_PRIVATE)

    /**
     * A stable identity for this phone. Derived from Settings.Secure.ANDROID_ID so it survives an
     * app reinstall (same signing key + device + user), which keeps the PC's stored pairing valid
     * across rebuilds instead of orphaning it under a fresh random id every install. Falls back to
     * a persisted random UUID when ANDROID_ID is unavailable or the known-buggy shared value.
     */
    val deviceId: String
        get() = prefs.getString("deviceId", null) ?: run {
            val androidId = runCatching {
                android.provider.Settings.Secure.getString(
                    appContext.contentResolver,
                    android.provider.Settings.Secure.ANDROID_ID,
                )
            }.getOrNull()
            val id = if (!androidId.isNullOrBlank() && androidId != "9774d56d682e549c") {
                "android-$androidId"
            } else {
                UUID.randomUUID().toString()
            }
            prefs.edit().putString("deviceId", id).apply()
            id
        }

    var deviceName: String
        get() = prefs.getString("deviceName", null) ?: "${Build.MANUFACTURER} ${Build.MODEL}".also {
            prefs.edit().putString("deviceName", it).apply()
        }
        set(v) = prefs.edit().putString("deviceName", v).apply()

    var privateKey: String?
        get() = prefs.getString("privateKey", null)
        set(v) = prefs.edit().putString("privateKey", v).apply()

    var publicKey: String?
        get() = prefs.getString("publicKey", null)
        set(v) = prefs.edit().putString("publicKey", v).apply()

    var autoAcceptFromPaired: Boolean
        get() = prefs.getBoolean("autoAccept", true)
        set(v) = prefs.edit().putBoolean("autoAccept", v).apply()

    // ---- Paired devices -------------------------------------------------------

    fun pairedDevices(): List<PairedDevice> {
        val raw = prefs.getString("paired", "[]") ?: "[]"
        val arr = JSONArray(raw)
        return (0 until arr.length()).map { i ->
            val o = arr.getJSONObject(i)
            PairedDevice(
                deviceId = o.getString("deviceId"),
                name = o.getString("name"),
                type = runCatching { DeviceType.valueOf(o.getString("type")) }.getOrDefault(DeviceType.UNKNOWN),
                publicKey = o.getString("publicKey"),
                pairedAt = o.optLong("pairedAt", System.currentTimeMillis()),
            )
        }
    }

    fun isPaired(deviceId: String): Boolean = pairedDevices().any { it.deviceId == deviceId }

    fun getPaired(deviceId: String): PairedDevice? = pairedDevices().firstOrNull { it.deviceId == deviceId }

    fun addPaired(device: PairedDevice) {
        val list = pairedDevices().filter { it.deviceId != device.deviceId } + device
        savePaired(list)
    }

    fun removePaired(deviceId: String) {
        savePaired(pairedDevices().filter { it.deviceId != deviceId })
    }

    private fun savePaired(list: List<PairedDevice>) {
        val arr = JSONArray()
        list.forEach { d ->
            arr.put(JSONObject().apply {
                put("deviceId", d.deviceId)
                put("name", d.name)
                put("type", d.type.name)
                put("publicKey", d.publicKey)
                put("pairedAt", d.pairedAt)
            })
        }
        prefs.edit().putString("paired", arr.toString()).apply()
    }
}
