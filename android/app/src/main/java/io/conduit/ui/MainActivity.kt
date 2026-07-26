package io.conduit.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.runtime.ConduitRuntime
import io.conduit.service.ConduitService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File

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
                Scaffold { padding ->
                    ConduitScreen(
                        modifier = Modifier.padding(padding),
                        onOpenNotificationAccess = {
                            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                        },
                    )
                }
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
private fun ConduitScreen(modifier: Modifier = Modifier, onOpenNotificationAccess: () -> Unit) {
    val context = LocalContext.current
    val devices by ConduitRuntime.devices.collectAsState()
    val connected by ConduitRuntime.connectedCount.collectAsState()
    val lastEvent by ConduitRuntime.lastEvent.collectAsState()
    val scope = remember { CoroutineScope(Dispatchers.Main) }
    var showLogs by remember { mutableStateOf(false) }

    Column(modifier = modifier.fillMaxSize().padding(16.dp)) {
        Text("Conduit", fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Text(
            "This device: ${ConduitRuntime.node?.self?.name ?: "starting…"}",
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text("Connected devices: $connected", color = MaterialTheme.colorScheme.primary)
        if (lastEvent.isNotEmpty()) Text(lastEvent, fontSize = 12.sp)

        Spacer(Modifier.height(12.dp))
        Row {
            Button(onClick = onOpenNotificationAccess) { Text("Enable notif mirroring") }
            Spacer(Modifier.width(8.dp))
            OutlinedButton(onClick = { showLogs = !showLogs }) {
                Text(if (showLogs) "Hide logs" else "View logs")
            }
        }

        Spacer(Modifier.height(12.dp))
        if (showLogs) {
            LogView()
        } else {
            Text("Nearby devices", fontWeight = FontWeight.SemiBold)
            LazyColumn {
                items(devices, key = { it.deviceId }) { device ->
                    DeviceCard(device) {
                        scope.launch {
                            try {
                                val node = ConduitRuntime.node ?: return@launch
                                if (device.isPaired) node.connect(device)
                                else {
                                    val code = node.startPairing(device)
                                    ConduitRuntime.lastEvent.value = "Pairing code: $code"
                                }
                            } catch (e: Exception) {
                                ConduitLog.tag("UI").w(e, "Action failed")
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun DeviceCard(device: DeviceInfo, onAction: () -> Unit) {
    val connected = ConduitRuntime.node?.isConnected(device.deviceId) == true
    Card(Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(12.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column {
                Text(device.name, fontWeight = FontWeight.SemiBold)
                Text(
                    when {
                        connected -> "Connected"
                        device.isPaired -> "Paired (offline)"
                        else -> "Discovered • ${device.ipAddress ?: "?"}"
                    },
                    fontSize = 12.sp,
                )
            }
            Button(onClick = onAction) {
                Text(if (device.isPaired) "Connect" else "Pair")
            }
        }
    }
}

@Composable
private fun LogView() {
    val context = LocalContext.current
    val text = remember {
        val dir = File(context.filesDir, "logs")
        val latest = dir.listFiles { f -> f.name.startsWith("conduit-") }
            ?.maxByOrNull { it.lastModified() }
        latest?.readText()?.lines()?.takeLast(300)?.joinToString("\n") ?: "(no logs yet)"
    }
    Text(
        text,
        fontFamily = FontFamily.Monospace,
        fontSize = 11.sp,
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
    )
}
