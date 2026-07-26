package io.conduit.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.conduit.R
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.runtime.ConduitRuntime
import io.conduit.service.ConduitService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { ConduitLog.tag("UI").i("Permission result: $it") }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestPermissions()
        ConduitService.start(this)
        setContent {
            ConduitTheme {
                ConduitScreen(
                    onOpenNotificationAccess = {
                        startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                    },
                )
            }
        }
    }

    private fun requestPermissions() {
        val perms = mutableListOf(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.READ_SMS,
            Manifest.permission.SEND_SMS,
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            perms.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        permissionLauncher.launch(perms.toTypedArray())
    }
}

@Composable
private fun ConduitScreen(onOpenNotificationAccess: () -> Unit) {
    val devices by ConduitRuntime.devices.collectAsState()
    val connected by ConduitRuntime.connectedCount.collectAsState()
    val lastEvent by ConduitRuntime.lastEvent.collectAsState()
    val scope = CoroutineScope(Dispatchers.Main)

    Box(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(listOf(NavyBg2, NavyBg))),
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
        ) {
            // ---- Header ----
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Image(
                    painter = painterResource(R.mipmap.ic_launcher_foreground),
                    contentDescription = null,
                    modifier = Modifier.size(44.dp),
                )
                Spacer(Modifier.width(6.dp))
                Column(Modifier.weight(1f)) {
                    Text("Conduit", color = TextHi, fontSize = 24.sp, fontWeight = FontWeight.Bold)
                    Text(
                        "This device · ${ConduitRuntime.node?.self?.name ?: "starting…"}",
                        color = TextMuted, fontSize = 12.sp,
                    )
                }
                ConnectionChip(connected > 0, if (connected > 0) "Connected" else "Searching")
            }

            Spacer(Modifier.height(18.dp))

            // ---- Pairing / event banner ----
            if (lastEvent.isNotEmpty()) {
                Card(
                    colors = CardDefaults.cardColors(containerColor = CardHi),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),
                ) {
                    Text(
                        lastEvent, color = Cyan, fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }

            // ---- Devices ----
            SectionLabel("NEARBY DEVICES")
            if (devices.isEmpty()) {
                EmptyState()
            } else {
                devices.forEach { device ->
                    DeviceCard(device) {
                        scope.launch {
                            try {
                                val node = ConduitRuntime.node ?: return@launch
                                if (device.isPaired) {
                                    node.connect(device)
                                    ConduitRuntime.lastEvent.value = "Connecting to ${device.name}…"
                                } else {
                                    val code = node.startPairing(device)
                                    ConduitRuntime.lastEvent.value =
                                        "Pairing with ${device.name} — confirm code $code on your PC"
                                }
                            } catch (e: Exception) {
                                ConduitLog.tag("UI").w(e, "Action failed")
                            }
                        }
                    }
                }
            }

            Spacer(Modifier.height(20.dp))

            // ---- Notification mirroring ----
            SectionLabel("FEATURES")
            Card(
                colors = CardDefaults.cardColors(containerColor = Card),
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Column(Modifier.padding(16.dp)) {
                    Text("Notification mirroring", color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                    Text(
                        "Show your phone's notifications on your PC. Requires notification access.",
                        color = TextMuted, fontSize = 13.sp,
                        modifier = Modifier.padding(top = 4.dp, bottom = 12.dp),
                    )
                    Button(
                        onClick = onOpenNotificationAccess,
                        colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                        shape = RoundedCornerShape(10.dp),
                        modifier = Modifier.fillMaxWidth().height(44.dp),
                    ) { Text("Enable notification access", fontWeight = FontWeight.SemiBold) }
                }
            }

            Spacer(Modifier.height(16.dp))
            Text(
                "Conduit connects over your local Wi-Fi. Nothing leaves your network.",
                color = TextMuted, fontSize = 11.sp,
            )
        }
    }
}

@Composable
private fun SectionLabel(text: String) {
    Text(
        text, color = TextMuted, fontSize = 12.sp, fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(bottom = 10.dp),
    )
}

@Composable
private fun ConnectionChip(connected: Boolean, label: String) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .clip(RoundedCornerShape(20.dp))
            .background(Card)
            .padding(horizontal = 12.dp, vertical = 7.dp),
    ) {
        Box(
            Modifier
                .size(9.dp)
                .clip(CircleShape)
                .background(if (connected) Success else TextMuted),
        )
        Spacer(Modifier.width(7.dp))
        Text(label, color = TextHi, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun DeviceCard(device: DeviceInfo, onAction: () -> Unit) {
    val connected = ConduitRuntime.node?.isConnected(device.deviceId) == true
    val statusColor = when {
        connected -> Success
        device.isPaired -> Warn
        else -> TextMuted
    }
    val statusText = when {
        connected -> "Connected"
        device.isPaired -> "Paired · offline"
        else -> "Discovered · ${device.ipAddress ?: "?"}"
    }
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(11.dp).clip(CircleShape).background(statusColor))
            Column(Modifier.weight(1f).padding(start = 12.dp)) {
                Text(device.name, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(statusText, color = TextMuted, fontSize = 12.sp)
            }
            if (connected) {
                Text("Connected", color = Success, fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
            } else {
                Button(
                    onClick = onAction,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = if (device.isPaired) CardHi else Cyan,
                        contentColor = if (device.isPaired) TextHi else NavyBg,
                    ),
                    shape = RoundedCornerShape(10.dp),
                ) { Text(if (device.isPaired) "Connect" else "Pair", fontWeight = FontWeight.SemiBold) }
            }
        }
    }
}

@Composable
private fun EmptyState() {
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(
            Modifier.fillMaxWidth().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text("Searching for devices…", color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
            Text(
                "Open Conduit on your PC and make sure it's on the same Wi-Fi network.",
                color = TextMuted, fontSize = 13.sp,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
    }
}
