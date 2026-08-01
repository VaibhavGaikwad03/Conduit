package io.conduit.features

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import io.conduit.logging.ConduitLog
import io.conduit.service.ConduitService

/**
 * A tiny, invisible activity whose only job is to show the system "Start screen capture?" dialog.
 * MediaProjection consent can only be requested from an Activity, but the PC's screen-start request
 * arrives in the background service — so the service launches this, and we hand the result back.
 */
class ScreenCaptureActivity : Activity() {
    private val log = ConduitLog.tag("Screen")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        try {
            startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST_CODE)
        } catch (e: Exception) {
            log.e(e, "Could not launch screen-capture consent")
            ConduitService.instance?.onScreenCaptureResult(RESULT_CANCELED, null)
            finish()
        }
    }

    @Deprecated("startActivityForResult result callback")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQUEST_CODE) {
            ConduitService.instance?.onScreenCaptureResult(resultCode, data)
        }
        finish()
    }

    companion object {
        private const val REQUEST_CODE = 7461

        /** Launch the consent dialog from a non-activity context (the service). */
        fun launch(context: Context) {
            context.startActivity(
                Intent(context, ScreenCaptureActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        }
    }
}
