package ru.lkxa.wgswitch.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.data.Settings
import ru.lkxa.wgswitch.vpn.State
import ru.lkxa.wgswitch.vpn.Status

@Composable
fun MainScreen(app: App, settings: Settings, live: Status, withVpn: (() -> Unit) -> Unit, notice: String?, clearNotice: () -> Unit) {
    val server = settings.selectedServer ?: return
    val context = currentContext()
    // статус живого туннеля — только если он и есть выбранный сервер; иначе выбранный выключен
    val status = if (live.name.equals(server.name, true)) live else Status(server.name)

    // вибрация на смену состояния — один раз на переход
    var prev by remember { mutableStateOf(status.state) }
    LaunchedEffect(status.state, status.name) {
        val was = prev
        prev = status.state
        if (was == status.state) return@LaunchedEffect
        when (status.state) {
            State.Connected -> if (was != State.NoResponse) app.haptics.success() else app.haptics.light()
            State.NoResponse -> app.haptics.error()
            State.Off -> if (was != State.Connecting || status.error == null) app.haptics.off() else app.haptics.error()
            State.Connecting -> {}
        }
    }

    Column(
        Modifier.fillMaxSize().padding(bottom = 24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        if (settings.servers.size > 1) {
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(bottom = 20.dp)) {
                settings.servers.forEach { s ->
                    val on = live.name.equals(s.name, true) && live.state != State.Off
                    val dot = when {
                        !on -> C.Faint
                        live.disconnecting || live.state == State.Connecting -> C.Amber
                        live.state == State.Connected -> C.Green
                        else -> C.Red
                    }
                    Chip(s.name, dot, s.name.equals(server.name, true)) {
                        app.haptics.light()
                        app.store.update { it.copy(selected = s.name) }
                    }
                }
            }
        }

        PowerButton(status, pendingOff = false, heartbeat = app.tunnels.heartbeat, haptics = app.haptics) {
            clearNotice()
            if (status.state != State.Off) app.tunnels.disconnect()
            else withVpn { app.tunnels.connect(server.name) }
        }

        val (accent, text) = when {
            status.disconnecting -> C.Amber to "Отключение"
            status.state == State.Connected -> C.Green to "Подключено"
            status.state == State.NoResponse -> C.Red to "Нет ответа от сервера"
            status.state == State.Connecting -> C.Amber to "Подключение"
            else -> C.Muted to "Выключено"
        }
        Row(Modifier.padding(top = 20.dp), verticalAlignment = Alignment.CenterVertically) {
            StateDot(accent, pinging = status.state == State.Connecting || status.disconnecting)
            Spacer(Modifier.width(12.dp))
            Rolling(text, T.state)
        }

        // имя · IP; касание копирует IP
        var copied by remember { mutableStateOf(false) }
        LaunchedEffect(copied) { if (copied) { delay(1200); copied = false } }
        val subColor by animateColorAsState(if (copied) C.Green else C.Muted, tween(250), label = "sub")
        Txt(
            if (copied) "скопировано ✓" else "${server.name} · ${server.pingHost}", T.value,
            Modifier.padding(top = 10.dp).press({ copy(context, server.pingHost); copied = true; app.haptics.light() }),
            color = subColor,
        )

        // сообщение: две строки зарезервированы, чтобы карточка не прыгала
        // handshake так и не прошёл — сервер не пустил телефон; прошёл, но пинг молчит — проблема внутри туннеля
        val message = notice ?: status.error ?: when {
            status.state != State.NoResponse -> null
            status.handshake == null -> "Сервер не ответил на handshake: добавлен ли ключ телефона в [Peer] на сервере?"
            else -> "Туннель поднят, но ${server.pingHost} не отвечает"
        }
        val msgAlpha by animateFloatAsState(if (message != null) 1f else 0f, tween(300), label = "msg")
        Txt(
            message ?: "", T.body, Modifier.padding(top = 16.dp).heightIn(min = 40.dp).fillMaxWidth().alpha(msgAlpha),
            color = if (notice != null || status.error != null) C.Red else C.Muted, align = TextAlign.Center,
        )

        StatusCard(status, settings.showDetails)
        UpdateLine(app)
    }
}

@Composable
private fun StatusCard(s: Status, details: Boolean) {
    val now by produceState(System.currentTimeMillis()) {
        while (true) {
            delay(1000)
            value = System.currentTimeMillis()
        }
    }
    Card(Modifier.padding(top = 8.dp)) {
        val ping = s.pingMs?.let { "$it мс" } ?: if (s.up) "нет ответа" else "—"
        InfoRow("Пинг", ping, valueColor = if (s.pingMs != null) C.Fg else if (s.up) C.Red else C.Faint)
        InfoRow("В сети", if (s.up && s.since != null) duration(now - s.since) else "—", valueColor = if (s.up) C.Fg else C.Faint)
        AnimatedVisibility(details, enter = expandVertically(tween(400, easing = Expo)) + fadeIn(), exit = shrinkVertically(tween(300)) + fadeOut()) {
            Column {
                InfoRow("Handshake", s.handshake?.let { ago(now - it) } ?: "—", valueColor = if (s.handshake != null) C.Fg else C.Faint)
                InfoRow(
                    "Трафик", if (s.rx != null && s.tx != null) "↓ ${bytes(s.rx)}  ↑ ${bytes(s.tx)}" else "—",
                    valueColor = if (s.rx != null) C.Fg else C.Faint,
                )
            }
        }
    }
}

fun duration(ms: Long): String {
    val t = ms / 1000
    val d = t / 86400
    val h = t / 3600 % 24
    val m = t / 60 % 60
    val s = t % 60
    return if (d > 0) "$d д %02d:%02d".format(h, m) else "%02d:%02d:%02d".format(t / 3600, m, s)
}

fun ago(ms: Long): String {
    val s = ms / 1000
    return when {
        s < 5 -> "только что"
        s < 60 -> "$s с назад"
        s < 3600 -> "${s / 60} мин назад"
        else -> "${s / 3600} ч назад"
    }
}

fun bytes(b: Long): String = when {
    b < 1024 -> "$b Б"
    b < 1024 * 1024 -> "%.1f КБ".format(b / 1024.0)
    b < 1024L * 1024 * 1024 -> "%.1f МБ".format(b / 1048576.0)
    else -> "%.2f ГБ".format(b / 1073741824.0)
}.replace(".0 ", " ")

