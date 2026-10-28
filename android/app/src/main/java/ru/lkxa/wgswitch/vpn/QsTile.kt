package ru.lkxa.wgswitch.vpn

import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.MainActivity

/** Плитка в шторке: включить или выключить выбранный сервер одним касанием. */
class QsTile : TileService() {
    private var watch: Job? = null

    override fun onStartListening() {
        val app = App.of(this)
        watch = CoroutineScope(Dispatchers.Main).launch {
            app.store.settings.combine(app.tunnels.status) { s, st -> s to st }.collect { (s, st) ->
                val tile = qsTile ?: return@collect
                val server = s.selectedServer
                val on = st.state != State.Off && st.name.equals(server?.name, true)
                tile.state = when {
                    server == null -> Tile.STATE_UNAVAILABLE
                    on -> Tile.STATE_ACTIVE
                    else -> Tile.STATE_INACTIVE
                }
                tile.label = server?.name ?: "WgSwitch"
                if (Build.VERSION.SDK_INT >= 29) tile.subtitle = when {
                    server == null -> "нет сервера"
                    !on -> "выключено"
                    st.disconnecting -> "отключение"
                    st.state == State.Connected -> st.pingMs?.let { "$it мс" } ?: "подключено"
                    st.state == State.NoResponse -> "нет ответа"
                    else -> "подключение"
                }
                tile.updateTile()
            }
        }
    }

    override fun onStopListening() {
        watch?.cancel()
        watch = null
    }

    override fun onClick() {
        val app = App.of(this)
        val server = app.store.current.selectedServer ?: return
        if (app.tunnels.isUp(server.name)) {
            app.tunnels.disconnect()
            return
        }
        if (VpnService.prepare(this) == null) {
            app.tunnels.connect(server.name)
            return
        }
        // разрешения на VPN ещё нет — его можно дать только из окна
        val intent = Intent(this, MainActivity::class.java).setAction(MainActivity.ACTION_CONNECT).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34)
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        else @Suppress("DEPRECATION") startActivityAndCollapse(intent)
    }
}
