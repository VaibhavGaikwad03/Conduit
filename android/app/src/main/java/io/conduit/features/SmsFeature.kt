package io.conduit.features

import android.content.Context
import android.provider.Telephony
import android.telephony.SmsManager
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import org.json.JSONArray
import org.json.JSONObject

/** Lists recent SMS threads for the PC and sends SMS on the PC's behalf. */
class SmsFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("Sms")

    fun sendThreadList() {
        try {
            val threads = JSONArray()
            val uri = Telephony.Sms.Inbox.CONTENT_URI
            val projection = arrayOf(
                Telephony.Sms.ADDRESS, Telephony.Sms.BODY, Telephony.Sms.DATE,
            )
            context.contentResolver.query(uri, projection, null, null, "${Telephony.Sms.DATE} DESC")
                ?.use { c ->
                    var count = 0
                    while (c.moveToNext() && count < 30) {
                        threads.put(JSONObject().apply {
                            put("address", c.getString(0) ?: "")
                            put("name", c.getString(0) ?: "")
                            put("snippet", (c.getString(1) ?: "").take(120))
                            put("ts", c.getLong(2))
                        })
                        count++
                    }
                }
            node.broadcast(Packet.create(PacketType.SMS_LIST) { put("threads", threads) })
            log.i("Sent ${threads.length()} SMS threads")
        } catch (e: Exception) {
            log.w(e, "Failed to read SMS (permission?)")
        }
    }

    fun send(address: String, body: String) {
        if (address.isEmpty() || body.isEmpty()) return
        try {
            @Suppress("DEPRECATION")
            val sms = SmsManager.getDefault()
            sms.sendTextMessage(address, null, body, null, null)
            log.i("Sent SMS to $address")
        } catch (e: Exception) {
            log.e(e, "Failed to send SMS")
        }
    }
}
