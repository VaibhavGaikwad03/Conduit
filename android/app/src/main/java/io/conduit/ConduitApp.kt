package io.conduit

import android.app.Application
import io.conduit.logging.ConduitLog

class ConduitApp : Application() {
    override fun onCreate() {
        super.onCreate()
        ConduitLog.init(this)
        ConduitLog.tag("App").i("Conduit application created")
    }
}
