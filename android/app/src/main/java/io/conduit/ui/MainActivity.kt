package io.conduit.ui

import android.Manifest
import android.app.admin.DevicePolicyManager
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import io.conduit.R
import io.conduit.features.ConduitDeviceAdminReceiver
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime
import io.conduit.runtime.SearchResultUi
import io.conduit.runtime.TransferUi
import io.conduit.service.ConduitService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.util.UUID

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
                    onEnableDeviceAdmin = {
                        val intent = Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN).apply {
                            putExtra(
                                DevicePolicyManager.EXTRA_DEVICE_ADMIN,
                                ConduitDeviceAdminReceiver.component(this@MainActivity),
                            )
                            putExtra(
                                DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                                "Allow Conduit to lock this phone from your PC.",
                            )
                        }
                        startActivity(intent)
                    },
                    onOpenAllFilesAccess = {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                            startActivity(
                                Intent(
                                    Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                                    Uri.parse("package:$packageName"),
                                ),
                            )
                        }
                    },
                )
            }
        }
    }

    private fun requestPermissions() {
        val perms = mutableListOf(
            Manifest.permission.READ_SMS,
            Manifest.permission.SEND_SMS,
            Manifest.permission.CAMERA,
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            perms.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        // Pre-Android-10 needs this to write received files into public Downloads.
        if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.P) {
            perms.add(Manifest.permission.WRITE_EXTERNAL_STORAGE)
        }
        permissionLauncher.launch(perms.toTypedArray())
    }
}

/**
 * Root of the app. A ☰ button opens a slide-out navigation drawer listing the devices;
 * tapping a device fills the main area with that device's actions (send file, clipboard,
 * pair/connect). The drawer also holds the phone-wide permission toggles.
 */
@Composable
private fun ConduitScreen(
    onOpenNotificationAccess: () -> Unit,
    onEnableDeviceAdmin: () -> Unit,
    onOpenAllFilesAccess: () -> Unit,
) {
    val devices by ConduitRuntime.devices.collectAsState()
    val connected by ConduitRuntime.connectedCount.collectAsState()
    val lastEvent by ConduitRuntime.lastEvent.collectAsState()
    val transfers by ConduitRuntime.transfers.collectAsState()
    val pendingPairing by ConduitRuntime.pendingPairing.collectAsState()
    val bgScope = CoroutineScope(Dispatchers.Main)
    val context = LocalContext.current

    // Notification / device-admin state — re-checked every time the app resumes.
    val lifecycleOwner = LocalLifecycleOwner.current
    var notifGranted by remember { mutableStateOf(isNotificationAccessGranted(context)) }
    var adminActive by remember { mutableStateOf(isDeviceAdminActive(context)) }
    var allFilesGranted by remember { mutableStateOf(isAllFilesAccessGranted()) }
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                notifGranted = isNotificationAccessGranted(context)
                adminActive = isDeviceAdminActive(context)
                allFilesGranted = isAllFilesAccessGranted()
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // The drawer, and which device is currently shown in the main area.
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val uiScope = rememberCoroutineScope()
    var openId by rememberSaveable { mutableStateOf<String?>(null) }
    var showSettings by rememberSaveable { mutableStateOf(false) }
    val connectedIds by ConduitRuntime.connectedIds.collectAsState()
    val openDevice = devices.firstOrNull { it.deviceId == openId }
    val openConnected = openDevice != null && openDevice.deviceId in connectedIds

    // Auto-pick a device the first time one connects, so the main area isn't empty.
    LaunchedEffect(devices) {
        if (openId == null) {
            openId = devices.firstOrNull { ConduitRuntime.node?.isConnected(it.deviceId) == true }?.deviceId
        }
    }

    // File picker: streams the chosen file to the open device.
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        val target = openId
        if (uri != null && target != null) {
            ConduitRuntime.files?.sendFile(target, uri)
            ConduitRuntime.lastEvent.value = "Sending file to ${openDevice?.name ?: "your PC"}…"
        }
    }

    fun sendClipboard(device: DeviceInfo) {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val text = cm.primaryClip?.getItemAt(0)?.coerceToText(context)?.toString().orEmpty()
        if (text.isBlank()) {
            ConduitRuntime.lastEvent.value = "Clipboard is empty"
            return
        }
        val ok = ConduitRuntime.node?.sendTo(
            device.deviceId,
            Packet.create(PacketType.CLIPBOARD) { put("content", text); put("contentType", "text") },
        ) ?: false
        ConduitRuntime.lastEvent.value =
            if (ok) "Clipboard sent to ${device.name}" else "Not connected to ${device.name}"
    }

    fun sendMediaCommand(device: DeviceInfo, command: String, value: Double? = null) {
        val ok = ConduitRuntime.node?.sendTo(
            device.deviceId,
            Packet.create(PacketType.MEDIA_COMMAND) {
                put("command", command)
                if (value != null) put("value", value)
            },
        ) ?: false
        if (!ok) ConduitRuntime.lastEvent.value = "Not connected to ${device.name}"
    }

    fun sendRemoteCommand(device: DeviceInfo, command: String) {
        val ok = ConduitRuntime.node?.sendTo(
            device.deviceId,
            Packet.create(PacketType.REMOTE_COMMAND) { put("command", command) },
        ) ?: false
        if (!ok) ConduitRuntime.lastEvent.value = "Not connected to ${device.name}"
    }

    // Drive the PC mouse/keyboard from the phone. Movement is relative (dx/dy), like a trackpad.
    fun sendPcInput(device: DeviceInfo, build: org.json.JSONObject.() -> Unit) {
        ConduitRuntime.node?.sendTo(device.deviceId, Packet.create(PacketType.PC_INPUT, build))
    }

    fun disconnect(device: DeviceInfo) {
        ConduitRuntime.node?.disconnect(device.deviceId)
        ConduitRuntime.lastEvent.value = "Disconnected from ${device.name}"
    }

    fun searchFiles(device: DeviceInfo, query: String) {
        val q = query.trim()
        if (q.length < 2) {
            ConduitRuntime.lastEvent.value = "Type at least 2 characters to search"
            return
        }
        ConduitRuntime.beginSearch()
        val ok = ConduitRuntime.node?.sendTo(
            device.deviceId,
            Packet.create(PacketType.FILE_SEARCH) {
                put("requestId", UUID.randomUUID().toString().replace("-", ""))
                put("query", q)
            },
        ) ?: false
        if (!ok) ConduitRuntime.lastEvent.value = "Not connected to ${device.name}"
    }

    fun downloadResult(result: SearchResultUi) {
        val ok = ConduitRuntime.node?.sendTo(
            result.deviceId,
            Packet.create(PacketType.FILE_REQUEST) { put("id", result.id) },
        ) ?: false
        ConduitRuntime.lastEvent.value =
            if (ok) "Requesting ${result.name}…" else "Not connected"
    }

    fun openLinkOnPc(device: DeviceInfo, url: String) {
        val u = url.trim()
        if (u.isEmpty()) return
        val ok = ConduitRuntime.node?.sendTo(
            device.deviceId,
            Packet.create(PacketType.OPEN_LINK) { put("url", u) },
        ) ?: false
        ConduitRuntime.lastEvent.value =
            if (ok) "Opening link on ${device.name}…" else "Not connected to ${device.name}"
    }

    fun pairOrConnect(device: DeviceInfo) {
        bgScope.launch {
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

    fun openDrawer() = uiScope.launch { drawerState.open() }
    fun closeDrawer() = uiScope.launch { drawerState.close() }

    // System back button: close the drawer, else leave settings.
    BackHandler(enabled = drawerState.isOpen || showSettings) {
        if (drawerState.isOpen) closeDrawer() else showSettings = false
    }

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(drawerContainerColor = NavyBg2) {
                DrawerContent(
                    selfName = ConduitRuntime.node?.self?.name,
                    devices = devices,
                    connectedIds = connectedIds,
                    openId = openId,
                    onOpenDevice = { openId = it.deviceId; showSettings = false; closeDrawer() },
                    onOpenSettings = { showSettings = true; closeDrawer() },
                )
            }
        },
    ) {
        if (showSettings) {
            SettingsScreen(
                notifGranted = notifGranted,
                adminActive = adminActive,
                allFilesGranted = allFilesGranted,
                onBack = { showSettings = false },
                onOpenNotificationAccess = onOpenNotificationAccess,
                onEnableDeviceAdmin = onEnableDeviceAdmin,
                onOpenAllFilesAccess = onOpenAllFilesAccess,
            )
        } else {
            MainContent(
                device = openDevice,
                connected = connected > 0,
                deviceConnected = openConnected,
                lastEvent = lastEvent,
                transfers = transfers,
                onMenu = { openDrawer() },
                onSendFile = { filePicker.launch("*/*") },
                onSendClipboard = { openDevice?.let { sendClipboard(it) } },
                onMediaCommand = { cmd, value -> openDevice?.let { sendMediaCommand(it, cmd, value) } },
                onRemoteCommand = { cmd -> openDevice?.let { sendRemoteCommand(it, cmd) } },
                onPcInput = { build -> openDevice?.let { sendPcInput(it, build) } },
                onSearch = { query -> openDevice?.let { searchFiles(it, query) } },
                onDownload = { result -> downloadResult(result) },
                onOpenLink = { url -> openDevice?.let { openLinkOnPc(it, url) } },
                onPairOrConnect = { openDevice?.let { pairOrConnect(it) } },
                onDisconnect = { openDevice?.let { disconnect(it) } },
            )
        }
    }

    // Incoming pair request: confirm the 6-digit code matches the other device before trusting it.
    pendingPairing?.let { prompt ->
        AlertDialog(
            onDismissRequest = { ConduitRuntime.answerPairing(false) },
            title = { Text("Pair with ${prompt.deviceName}?") },
            text = {
                Column {
                    Text("Only pair if this code matches the one shown on ${prompt.deviceName}:",
                        color = TextMuted, fontSize = 13.sp)
                    Spacer(Modifier.height(12.dp))
                    Text(prompt.code, color = Cyan, fontSize = 30.sp,
                        fontWeight = FontWeight.Bold, letterSpacing = 6.sp,
                        modifier = Modifier.fillMaxWidth(), textAlign = TextAlign.Center)
                }
            },
            confirmButton = {
                TextButton(onClick = { ConduitRuntime.answerPairing(true) }) {
                    Text("Pair", color = Cyan, fontWeight = FontWeight.SemiBold)
                }
            },
            dismissButton = {
                TextButton(onClick = { ConduitRuntime.answerPairing(false) }) {
                    Text("Reject", color = Warn)
                }
            },
        )
    }
}

// ---- Drawer (the flyout) --------------------------------------------------------

@Composable
private fun DrawerContent(
    selfName: String?,
    devices: List<DeviceInfo>,
    connectedIds: Set<String>,
    openId: String?,
    onOpenDevice: (DeviceInfo) -> Unit,
    onOpenSettings: () -> Unit,
) {
    Column(
        Modifier
            .fillMaxHeight()
            .verticalScroll(rememberScrollState())
            .padding(18.dp),
    ) {
        // Header
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Image(
                painter = painterResource(R.mipmap.ic_launcher_foreground),
                contentDescription = null,
                modifier = Modifier.size(40.dp),
            )
            Spacer(Modifier.width(6.dp))
            Column(Modifier.weight(1f)) {
                Text("Conduit", color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Text("This device · ${selfName ?: "starting…"}", color = TextMuted, fontSize = 11.sp)
            }
        }

        Spacer(Modifier.height(18.dp))

        SectionLabel("NEARBY DEVICES")
        if (devices.isEmpty()) {
            Text("Searching for devices…", color = TextMuted, fontSize = 13.sp)
        } else {
            devices.forEach { device ->
                DeviceListRow(
                    device,
                    connected = device.deviceId in connectedIds,
                    selected = device.deviceId == openId,
                ) { onOpenDevice(device) }
            }
        }

        Spacer(Modifier.height(22.dp))

        SectionLabel("APP")
        Card(
            onClick = onOpenSettings,
            colors = CardDefaults.cardColors(containerColor = Card),
            shape = RoundedCornerShape(12.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Row(
                Modifier.fillMaxWidth().padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("⚙", fontSize = 20.sp)
                Column(Modifier.weight(1f).padding(start = 12.dp)) {
                    Text("Settings", color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                    Text("Notification mirroring, remote lock", color = TextMuted, fontSize = 11.sp)
                }
                Text("›", color = TextMuted, fontSize = 22.sp, fontWeight = FontWeight.Bold)
            }
        }
    }
}

@Composable
private fun SettingsScreen(
    notifGranted: Boolean,
    adminActive: Boolean,
    allFilesGranted: Boolean,
    onBack: () -> Unit,
    onOpenNotificationAccess: () -> Unit,
    onEnableDeviceAdmin: () -> Unit,
    onOpenAllFilesAccess: () -> Unit,
) {
    Column(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(listOf(NavyBg2, NavyBg))),
    ) {
        // Top bar with back arrow
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                "‹",
                color = Cyan, fontSize = 34.sp, fontWeight = FontWeight.Bold,
                modifier = Modifier
                    .clip(CircleShape)
                    .clickable(onClick = onBack)
                    .padding(horizontal = 10.dp),
            )
            Spacer(Modifier.width(4.dp))
            Text("Settings", color = TextHi, fontSize = 22.sp, fontWeight = FontWeight.Bold)
        }

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp),
        ) {
            SectionLabel("THIS PHONE")
            FeatureToggleCard(
                title = "Notification mirroring",
                enabledText = "Your phone's notifications appear on your PC.",
                disabledText = "Show your phone's notifications on your PC. Requires notification access.",
                enabledButton = "Manage access",
                disabledButton = "Enable notification access",
                granted = notifGranted,
                onClick = onOpenNotificationAccess,
            )
            Spacer(Modifier.height(12.dp))
            FeatureToggleCard(
                title = "Remote lock",
                enabledText = "Your PC can lock this phone's screen.",
                disabledText = "Let your PC lock this phone. Requires device-admin permission.",
                enabledButton = "Manage",
                disabledButton = "Enable remote lock",
                granted = adminActive,
                onClick = onEnableDeviceAdmin,
            )
            Spacer(Modifier.height(12.dp))
            FeatureToggleCard(
                title = "All-file search",
                enabledText = "Your PC can search every file on this phone, including APKs, docs and zips.",
                disabledText = "Without this, PC search only finds photos, videos, audio and Downloads. " +
                    "Enable 'All files access' to search every file.",
                enabledButton = "Manage access",
                disabledButton = "Enable all-file search",
                granted = allFilesGranted,
                onClick = onOpenAllFilesAccess,
            )
            Spacer(Modifier.height(20.dp))
            Text(
                "Conduit connects over your local Wi-Fi. Nothing leaves your network.",
                color = TextMuted, fontSize = 11.sp,
            )
        }
    }
}

@Composable
private fun DeviceListRow(device: DeviceInfo, connected: Boolean, selected: Boolean, onClick: () -> Unit) {
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
        onClick = onClick,
        colors = CardDefaults.cardColors(containerColor = if (selected) CardHi else Card),
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(10.dp).clip(CircleShape).background(statusColor))
            Column(Modifier.weight(1f).padding(start = 10.dp)) {
                Text(device.name, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(statusText, color = TextMuted, fontSize = 11.sp)
            }
        }
    }
}

// ---- Main area ------------------------------------------------------------------

@Composable
private fun MainContent(
    device: DeviceInfo?,
    connected: Boolean,
    deviceConnected: Boolean,
    lastEvent: String,
    transfers: List<TransferUi>,
    onMenu: () -> Unit,
    onSendFile: () -> Unit,
    onSendClipboard: () -> Unit,
    onMediaCommand: (String, Double?) -> Unit,
    onRemoteCommand: (String) -> Unit,
    onPcInput: (org.json.JSONObject.() -> Unit) -> Unit,
    onSearch: (String) -> Unit,
    onDownload: (SearchResultUi) -> Unit,
    onOpenLink: (String) -> Unit,
    onPairOrConnect: () -> Unit,
    onDisconnect: () -> Unit,
) {
    Column(
        Modifier
            .fillMaxSize()
            .background(Brush.verticalGradient(listOf(NavyBg2, NavyBg))),
    ) {
        // ---- Top bar with the ☰ flyout button ----
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                "☰",
                color = TextHi, fontSize = 26.sp,
                modifier = Modifier
                    .clip(RoundedCornerShape(10.dp))
                    .clickable(onClick = onMenu)
                    .padding(horizontal = 10.dp, vertical = 4.dp),
            )
            Spacer(Modifier.width(6.dp))
            Text(
                device?.name ?: "Conduit",
                color = TextHi, fontSize = 20.sp, fontWeight = FontWeight.Bold,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            ConnectionChip(connected, if (connected) "Connected" else "Searching")
        }

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp),
        ) {
            // ---- Event banner ----
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

            if (device == null) {
                DevicePickerHint()
            } else {
                DeviceDetail(
                    device = device,
                    connected = deviceConnected,
                    onSendFile = onSendFile,
                    onSendClipboard = onSendClipboard,
                    onMediaCommand = onMediaCommand,
                    onRemoteCommand = onRemoteCommand,
                    onPcInput = onPcInput,
                    onSearch = onSearch,
                    onDownload = onDownload,
                    onOpenLink = onOpenLink,
                    onPairOrConnect = onPairOrConnect,
                    onDisconnect = onDisconnect,
                )
            }

            // ---- Active transfers ----
            if (transfers.isNotEmpty()) {
                Spacer(Modifier.height(20.dp))
                SectionLabel("FILE TRANSFERS")
                Card(
                    colors = CardDefaults.cardColors(containerColor = Card),
                    shape = RoundedCornerShape(14.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Column(Modifier.padding(16.dp)) { transfers.forEach { TransferItem(it) } }
                }
            }

            Spacer(Modifier.height(20.dp))
        }
    }
}

@Composable
private fun DevicePickerHint() {
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(
            Modifier.fillMaxWidth().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text("Pick a device", color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text(
                "Tap ☰ at the top-left to open the device list, then choose a device to send files and clipboard.",
                color = TextMuted, fontSize = 13.sp,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
    }
}

@Composable
private fun DeviceDetail(
    device: DeviceInfo,
    connected: Boolean,
    onSendFile: () -> Unit,
    onSendClipboard: () -> Unit,
    onMediaCommand: (String, Double?) -> Unit,
    onRemoteCommand: (String) -> Unit,
    onPcInput: (org.json.JSONObject.() -> Unit) -> Unit,
    onSearch: (String) -> Unit,
    onDownload: (SearchResultUi) -> Unit,
    onOpenLink: (String) -> Unit,
    onPairOrConnect: () -> Unit,
    onDisconnect: () -> Unit,
) {
    val statusColor = when {
        connected && device.isPaired -> Success
        connected -> Warn            // connected but not paired — can't use features yet
        device.isPaired -> Warn
        else -> TextMuted
    }
    val statusText = when {
        connected && device.isPaired -> "Connected"
        connected -> "Connected · not paired"
        device.isPaired -> "Paired · offline"
        else -> "Discovered · ${device.ipAddress ?: "?"}"
    }

    // Status line
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(bottom = 16.dp)) {
        Box(Modifier.size(10.dp).clip(CircleShape).background(statusColor))
        Spacer(Modifier.width(8.dp))
        Text(statusText, color = TextMuted, fontSize = 13.sp)
    }

    if (connected && device.isPaired) {
        SectionLabel("SEND TO THIS DEVICE")
        Card(
            colors = CardDefaults.cardColors(containerColor = Card),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Column {
                ActionRow("📎", "Send file", "Pick a file to send to this device", onSendFile)
                RowDivider()
                ActionRow("📋", "Send clipboard", "Copy text here, then send it over", onSendClipboard)
            }
        }
        Spacer(Modifier.height(20.dp))
        SectionLabel("OPEN LINK ON PC")
        OpenLinkCard(onOpenLink)
        Spacer(Modifier.height(20.dp))
        SectionLabel("SEARCH FILES ON PC")
        FileSearchCard(onSearch, onDownload)
        Spacer(Modifier.height(20.dp))
        SectionLabel("TOUCHPAD")
        TouchpadCard(onPcInput)
        Spacer(Modifier.height(20.dp))
        SectionLabel("CONTROL PC")
        ControlPcCard(onRemoteCommand)
        Spacer(Modifier.height(20.dp))
        SectionLabel("CONTROL PC MEDIA")
        MediaRemoteCard(onMediaCommand)
        Spacer(Modifier.height(12.dp))
        OutlinedButton(
            onClick = onDisconnect,
            shape = RoundedCornerShape(10.dp),
            modifier = Modifier.fillMaxWidth().height(46.dp),
        ) { Text("Disconnect", color = Warn, fontWeight = FontWeight.SemiBold) }
    } else {
        Card(
            colors = CardDefaults.cardColors(containerColor = Card),
            shape = RoundedCornerShape(14.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Column(Modifier.padding(16.dp)) {
                Text(
                    when {
                        connected && !device.isPaired ->
                            "Connected, but not paired yet. Tap Pair, then confirm the matching code on your PC to unlock features."
                        device.isPaired ->
                            "This device is paired but not connected right now. Connect to send files and clipboard."
                        else ->
                            "Pair with this device first. You'll confirm a 6-digit code on your PC."
                    },
                    color = TextMuted, fontSize = 13.sp,
                    modifier = Modifier.padding(bottom = 14.dp),
                )
                Button(
                    onClick = onPairOrConnect,
                    colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier.fillMaxWidth().height(46.dp),
                ) {
                    Text(if (device.isPaired) "Connect" else "Pair", fontWeight = FontWeight.SemiBold)
                }
            }
        }
    }
}

/**
 * A remote for whatever is playing on the PC. Each button sends a media-command packet the
 * Windows side turns into a hardware media key, so it drives Spotify, browsers, etc. without
 * any per-app integration. Volume nudges one step at a time (value either side of 0.5).
 */
@Composable
private fun MediaRemoteCard(onCommand: (String, Double?) -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                MediaButton("⏮", Modifier.weight(1f)) { onCommand("prev", null) }
                MediaButton("⏯", Modifier.weight(1f), primary = true) { onCommand("pause", null) }
                MediaButton("⏭", Modifier.weight(1f)) { onCommand("next", null) }
            }
            Spacer(Modifier.height(10.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                MediaButton("🔉", Modifier.weight(1f)) { onCommand("volume", 0.4) }
                MediaButton("🔇", Modifier.weight(1f)) { onCommand("mute", null) }
                MediaButton("🔊", Modifier.weight(1f)) { onCommand("volume", 0.6) }
            }
        }
    }
}

/**
 * System controls for the PC: lock, sleep, "find my PC" (beeps + pops the window), and shut
 * down. Each sends a remote-command packet the Windows PowerService executes. Shut down asks
 * for confirmation first so a stray tap can't power the PC off. (Volume lives in the media card.)
 */
@Composable
private fun ControlPcCard(onCommand: (String) -> Unit) {
    var confirmShutdown by remember { mutableStateOf(false) }
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                PcButton("🔒", "Lock", Modifier.weight(1f)) { onCommand("lock") }
                PcButton("😴", "Sleep", Modifier.weight(1f)) { onCommand("sleep") }
                PcButton("🔔", "Find PC", Modifier.weight(1f)) { onCommand("findpc") }
            }
            Spacer(Modifier.height(10.dp))
            OutlinedButton(
                onClick = { confirmShutdown = true },
                shape = RoundedCornerShape(10.dp),
                modifier = Modifier.fillMaxWidth().height(46.dp),
            ) { Text("⏻  Shut down PC", color = Warn, fontWeight = FontWeight.SemiBold) }
        }
    }

    if (confirmShutdown) {
        AlertDialog(
            onDismissRequest = { confirmShutdown = false },
            title = { Text("Shut down PC?") },
            text = { Text("This will power off your PC now, closing everything.") },
            confirmButton = {
                TextButton(onClick = { confirmShutdown = false; onCommand("shutdown") }) {
                    Text("Shut down", color = Warn)
                }
            },
            dismissButton = { TextButton(onClick = { confirmShutdown = false }) { Text("Cancel") } },
        )
    }
}

/**
 * Turns the phone into a trackpad for the PC: drag to move the cursor (relative, like a laptop
 * touchpad), tap to left-click, long-press to right-click. Buttons underneath add explicit
 * clicks and scrolling, and the text field types straight into the focused PC window.
 */
@Composable
private fun TouchpadCard(onPcInput: (org.json.JSONObject.() -> Unit) -> Unit) {
    val sensitivity = 1.6f
    var text by rememberSaveable { mutableStateOf("") }

    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(170.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(CardHi)
                    .border(1.dp, Cyan.copy(alpha = 0.35f), RoundedCornerShape(12.dp))
                    .pointerInput(Unit) {
                        detectTapGestures(
                            onTap = { onPcInput { put("action", "click"); put("button", "left") } },
                            onLongPress = { onPcInput { put("action", "click"); put("button", "right") } },
                        )
                    }
                    .pointerInput(Unit) {
                        // Send raw deltas (with sub-pixel carry so slow drags aren't lost to
                        // rounding). The PC eases the cursor toward these at ~200 Hz, which is
                        // what makes the motion smooth between packets rather than steppy.
                        var accX = 0f; var accY = 0f
                        detectDragGestures(
                            onDragStart = { accX = 0f; accY = 0f },
                            onDrag = { change, drag ->
                                change.consume()
                                accX += drag.x * sensitivity
                                accY += drag.y * sensitivity
                                val dx = accX.toInt(); val dy = accY.toInt()
                                if (dx != 0 || dy != 0) {
                                    accX -= dx; accY -= dy
                                    onPcInput { put("action", "move"); put("dx", dx); put("dy", dy) }
                                }
                            },
                        )
                    },
                contentAlignment = Alignment.Center,
            ) {
                Text("Drag to move · tap = click · long-press = right-click",
                    color = TextMuted, fontSize = 12.sp)
            }
            Spacer(Modifier.height(10.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                PcButton("🖱", "Left", Modifier.weight(1f)) {
                    onPcInput { put("action", "click"); put("button", "left") }
                }
                PcButton("🖱", "Right", Modifier.weight(1f)) {
                    onPcInput { put("action", "click"); put("button", "right") }
                }
                PcButton("⬆", "Scroll", Modifier.weight(1f)) {
                    onPcInput { put("action", "scroll"); put("amount", 120) }
                }
                PcButton("⬇", "Scroll", Modifier.weight(1f)) {
                    onPcInput { put("action", "scroll"); put("amount", -120) }
                }
            }
            Spacer(Modifier.height(12.dp))
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    singleLine = true,
                    placeholder = { Text("Type to PC", color = TextMuted) },
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(10.dp))
                Button(
                    onClick = {
                        if (text.isNotEmpty()) { onPcInput { put("action", "text"); put("text", text) }; text = "" }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier.height(52.dp),
                ) { Text("Send", fontWeight = FontWeight.SemiBold) }
            }
            Spacer(Modifier.height(8.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                PcButton("⏎", "Enter", Modifier.weight(1f)) {
                    onPcInput { put("action", "key"); put("key", "enter") }
                }
                PcButton("⌫", "Back", Modifier.weight(1f)) {
                    onPcInput { put("action", "key"); put("key", "backspace") }
                }
            }
        }
    }
}

/** A labelled control button (emoji over a caption) used by the Control-PC and Touchpad cards. */
@Composable
private fun PcButton(emoji: String, label: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        shape = RoundedCornerShape(10.dp),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(vertical = 8.dp),
        modifier = modifier.height(56.dp),
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(emoji, fontSize = 18.sp)
            Text(label, fontSize = 11.sp, color = TextHi)
        }
    }
}

/** Send a URL to open in the PC's default browser. */
@Composable
private fun OpenLinkCard(onOpenLink: (String) -> Unit) {
    var url by rememberSaveable { mutableStateOf("") }
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedTextField(
                value = url,
                onValueChange = { url = it },
                singleLine = true,
                placeholder = { Text("https://…", color = TextMuted) },
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(10.dp))
            Button(
                onClick = { onOpenLink(url); url = "" },
                colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                shape = RoundedCornerShape(10.dp),
                modifier = Modifier.height(52.dp),
            ) { Text("Open", fontWeight = FontWeight.SemiBold) }
        }
    }
}

/**
 * Search the connected peer's files by name and download any match. Results stream in from
 * ConduitRuntime after the peer replies; tapping ⬇ pulls the file via the normal transfer path.
 */
@Composable
private fun FileSearchCard(onSearch: (String) -> Unit, onDownload: (SearchResultUi) -> Unit) {
    val results by ConduitRuntime.searchResults.collectAsState()
    val pending by ConduitRuntime.searchPending.collectAsState()
    val truncated by ConduitRuntime.searchTruncated.collectAsState()
    var query by rememberSaveable { mutableStateOf("") }
    var searched by rememberSaveable { mutableStateOf(false) }

    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = query,
                    onValueChange = { query = it },
                    singleLine = true,
                    placeholder = { Text("File name", color = TextMuted) },
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(10.dp))
                Button(
                    onClick = { searched = true; onSearch(query) },
                    colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier.height(52.dp),
                ) { Text("Search", fontWeight = FontWeight.SemiBold) }
            }

            when {
                pending -> StatusLine("Searching…")
                searched && results.isEmpty() -> StatusLine("No matching files")
                results.isNotEmpty() -> {
                    Spacer(Modifier.height(6.dp))
                    StatusLine("${results.size} result${if (results.size == 1) "" else "s"}" +
                        if (truncated) " · showing first 100" else "")
                    results.forEach { SearchResultItem(it, onDownload) }
                }
                else -> StatusLine("Search this device's Downloads, Documents and media by name.")
            }
        }
    }
}

@Composable
private fun StatusLine(text: String) {
    Text(text, color = TextMuted, fontSize = 12.sp, modifier = Modifier.padding(top = 10.dp))
}

@Composable
private fun SearchResultItem(r: SearchResultUi, onDownload: (SearchResultUi) -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(top = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f).padding(end = 8.dp)) {
            Text(
                r.name, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 14.sp,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
            val detail = if (r.folder.isBlank()) formatSize(r.size) else "${r.folder} · ${formatSize(r.size)}"
            Text(detail, color = TextMuted, fontSize = 11.sp)
        }
        OutlinedButton(
            onClick = { onDownload(r) },
            shape = RoundedCornerShape(10.dp),
            modifier = Modifier.height(40.dp),
        ) { Text("⬇  Get", color = TextHi, fontWeight = FontWeight.SemiBold) }
    }
}

private fun formatSize(bytes: Long): String {
    if (bytes <= 0) return "0 B"
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var size = bytes.toDouble()
    var i = 0
    while (size >= 1024 && i < units.size - 1) { size /= 1024; i++ }
    return if (i == 0) "${bytes} B" else String.format("%.1f %s", size, units[i])
}

@Composable
private fun MediaButton(label: String, modifier: Modifier = Modifier, primary: Boolean = false, onClick: () -> Unit) {
    if (primary) {
        Button(
            onClick = onClick,
            colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
            shape = RoundedCornerShape(10.dp),
            modifier = modifier.height(48.dp),
        ) { Text(label, fontSize = 20.sp) }
    } else {
        OutlinedButton(
            onClick = onClick,
            shape = RoundedCornerShape(10.dp),
            modifier = modifier.height(48.dp),
        ) { Text(label, fontSize = 20.sp, color = TextHi) }
    }
}

@Composable
private fun ActionRow(icon: String, title: String, subtitle: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(icon, fontSize = 22.sp)
        Column(Modifier.weight(1f).padding(start = 14.dp)) {
            Text(title, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
            Text(subtitle, color = TextMuted, fontSize = 12.sp)
        }
        Text("›", color = TextMuted, fontSize = 22.sp, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun RowDivider() {
    Box(Modifier.fillMaxWidth().padding(horizontal = 16.dp).height(1.dp).background(Stroke))
}

// ---- Shared pieces --------------------------------------------------------------

@Composable
private fun FeatureToggleCard(
    title: String,
    enabledText: String,
    disabledText: String,
    enabledButton: String,
    disabledButton: String,
    granted: Boolean,
    onClick: () -> Unit,
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = Card),
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                Text(
                    title, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 15.sp,
                    modifier = Modifier.weight(1f),
                )
                if (granted) {
                    Box(Modifier.size(9.dp).clip(CircleShape).background(Success))
                    Spacer(Modifier.width(6.dp))
                    Text("On", color = Success, fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                }
            }
            Text(
                if (granted) enabledText else disabledText,
                color = TextMuted, fontSize = 13.sp,
                modifier = Modifier.padding(top = 4.dp, bottom = 12.dp),
            )
            if (granted) {
                OutlinedButton(
                    onClick = onClick,
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier.fillMaxWidth().height(44.dp),
                ) { Text(enabledButton, color = TextHi, fontWeight = FontWeight.SemiBold) }
            } else {
                Button(
                    onClick = onClick,
                    colors = ButtonDefaults.buttonColors(containerColor = Cyan, contentColor = NavyBg),
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier.fillMaxWidth().height(44.dp),
                ) { Text(disabledButton, fontWeight = FontWeight.SemiBold) }
            }
        }
    }
}

@Composable
private fun TransferItem(t: TransferUi) {
    val barColor = when {
        t.failed -> MaterialTheme.colorScheme.error
        t.done -> Success
        else -> Cyan
    }
    val status = when {
        t.failed -> "Failed"
        t.done -> if (t.sending) "Sent ✓" else "Saved to Downloads ✓"
        else -> "${if (t.sending) "Sending" else "Receiving"} · ${t.percent}%"
    }
    Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                t.name, color = TextHi, fontWeight = FontWeight.SemiBold, fontSize = 13.sp,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f).padding(end = 8.dp),
            )
            Text(status, color = if (t.done) Success else TextMuted, fontSize = 12.sp)
        }
        LinearProgressIndicator(
            progress = { t.percent / 100f },
            color = barColor,
            trackColor = Stroke,
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 6.dp)
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp)),
        )
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

/** True when the user has granted Conduit notification access (the listener is enabled). */
private fun isNotificationAccessGranted(context: Context): Boolean =
    NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)

/** True when Conduit holds "All files access" (so file search can see every file, not just media). */
private fun isAllFilesAccessGranted(): Boolean =
    Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && Environment.isExternalStorageManager()

/** True when Conduit is an active Device Admin (so the PC can lock the phone). */
private fun isDeviceAdminActive(context: Context): Boolean {
    val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
    return dpm.isAdminActive(ConduitDeviceAdminReceiver.component(context))
}
