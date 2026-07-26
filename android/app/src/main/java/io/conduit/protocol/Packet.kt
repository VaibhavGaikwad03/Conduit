package io.conduit.protocol

import org.json.JSONObject
import java.util.UUID

/** Canonical packet `type` values. Must match PROTOCOL.md and the Windows side. */
object PacketType {
    const val IDENTITY = "identity"
    const val PAIR_REQUEST = "pair-request"
    const val PAIR_RESPONSE = "pair-response"
    const val PING = "ping"
    const val PONG = "pong"
    const val CLIPBOARD = "clipboard"
    const val FILE_OFFER = "file-offer"
    const val FILE_CHUNK = "file-chunk"
    const val FILE_COMPLETE = "file-complete"
    const val NOTIFICATION = "notification"
    const val NOTIFICATION_ACTION = "notification-action"
    const val MEDIA_STATE = "media-state"
    const val MEDIA_COMMAND = "media-command"
    const val REMOTE_COMMAND = "remote-command"
    const val BATTERY = "battery"
    const val DEVICE_STATUS = "device-status"
    const val SMS_LIST = "sms-list"
    const val SMS_SEND = "sms-send"
    const val ERROR = "error"
}

/**
 * The envelope every message travels in: { id, type, ts, body }.
 * See PROTOCOL.md §3.
 */
class Packet private constructor(
    val id: String,
    val type: String,
    val ts: Long,
    val body: JSONObject,
) {
    fun getString(key: String): String? = if (body.has(key)) body.optString(key) else null
    fun getLong(key: String, fallback: Long = 0L): Long = body.optLong(key, fallback)
    fun getInt(key: String, fallback: Int = 0): Int = body.optInt(key, fallback)
    fun getBool(key: String, fallback: Boolean = false): Boolean = body.optBoolean(key, fallback)
    fun getDouble(key: String): Double? = if (body.has(key)) body.optDouble(key) else null

    fun toJson(): String = JSONObject().apply {
        put("id", id)
        put("type", type)
        put("ts", ts)
        put("body", body)
    }.toString()

    companion object {
        fun create(type: String, build: (JSONObject.() -> Unit)? = null): Packet {
            val body = JSONObject()
            build?.invoke(body)
            return Packet(UUID.randomUUID().toString().replace("-", ""), type, System.currentTimeMillis(), body)
        }

        fun fromJson(json: String): Packet {
            val obj = JSONObject(json)
            return Packet(
                id = obj.optString("id", UUID.randomUUID().toString()),
                type = obj.optString("type", PacketType.ERROR),
                ts = obj.optLong("ts", System.currentTimeMillis()),
                body = obj.optJSONObject("body") ?: JSONObject(),
            )
        }
    }
}
