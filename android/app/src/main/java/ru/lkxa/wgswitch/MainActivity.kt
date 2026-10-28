package ru.lkxa.wgswitch

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.graphics.Color
import android.net.VpnService
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.mutableStateOf
import ru.lkxa.wgswitch.ui.WgSwitchApp

class MainActivity : ComponentActivity() {
    private var afterVpn: (() -> Unit)? = null
    private val vpnDenied = mutableStateOf(0)

    private val notifyPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) App.of(this).statusUi.refresh()
    }

    /** Android 13+: уведомление в шторке без разрешения не появится. Спрашиваем, пока пользователь не отказал насовсем. */
    private fun askNotifications() {
        if (Build.VERSION.SDK_INT < 33) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        notifyPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    private val vpnPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { r ->
        val action = afterVpn
        afterVpn = null
        if (r.resultCode == RESULT_OK) action?.invoke() else vpnDenied.value++
    }

    /** Android спрашивает разрешение на VPN один раз; дальше prepare() возвращает null. */
    private fun withVpn(action: () -> Unit) {
        val ask = try { VpnService.prepare(this) } catch (_: Exception) { null }
        if (ask == null) action() else {
            afterVpn = action
            vpnPermission.launch(ask)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        super.onCreate(savedInstanceState)
        val app = App.of(this)
        setContent { WgSwitchApp(app, ::withVpn, vpnDenied, ::askNotifications) }
        if (savedInstanceState == null && app.store.current.notify) askNotifications()
        handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    /** Плитка в шторке открывает окно, только если разрешения на VPN ещё нет. */
    private fun handle(intent: Intent?) {
        if (intent?.action != ACTION_CONNECT) return
        val app = App.of(this)
        val server = app.store.current.selectedServer ?: return
        withVpn { app.tunnels.connect(server.name) }
    }

    companion object {
        const val ACTION_CONNECT = "ru.lkxa.wgswitch.CONNECT"
    }
}
