package ru.lkxa.wgswitch.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.data.Settings
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Панель сервера, как на ПК: SSH и SFTP — в установленных приложениях по ссылкам ssh:// и sftp://,
 * веб-панель — в браузере. Точка у строки — открыт ли порт через туннель; проверка раз в 5 секунд, пока экран открыт.
 */
@Composable
fun ToolsScreen(app: App, settings: Settings) {
    val server = settings.selectedServer ?: return
    val context = currentContext()
    val live by app.tunnels.status.collectAsState()
    val up = live.up && live.name.equals(server.name, true)

    var user by remember(server.name) { mutableStateOf(server.sshUser) }
    var port by remember(server.name) { mutableStateOf(server.sshPort.toString()) }
    var panel by remember(server.name) { mutableStateOf(server.panelUrl ?: "") }
    var sshOk by remember { mutableStateOf<Boolean?>(null) }
    var panelOk by remember { mutableStateOf<Boolean?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }

    val panelUri = server.panelUrl?.let { runCatching { Uri.parse(it) }.getOrNull() }?.takeIf { it.host != null }
    val sshUri = "ssh://${server.sshUser}@${server.pingHost}:${server.sshPort}"
    val sftpUri = "sftp://${server.sshUser}@${server.pingHost}:${server.sshPort}"
    val hasSftp = remember(sftpUri) { canOpen(context, sftpUri) }

    fun save() {
        val u = user.trim().ifEmpty { "root" }
        val p = port.trim().toIntOrNull()?.takeIf { it in 1..65535 } ?: 22
        val raw = panel.trim()
        val url = if (raw.isEmpty()) null else if ("://" in raw) raw else "http://$raw"
        user = u; port = p.toString(); panel = url ?: ""
        if (u != server.sshUser || p != server.sshPort || url != server.panelUrl)
            app.store.updateServer(server.name) { it.copy(sshUser = u, sshPort = p, panelUrl = url) }
    }

    LaunchedEffect(up, server.sshPort, server.panelUrl, server.pingHost) {
        if (!up) { sshOk = null; panelOk = null; return@LaunchedEffect }
        while (true) {
            val ssh = async(Dispatchers.IO) { probe(server.pingHost, server.sshPort) }
            val web = async(Dispatchers.IO) { panelUri?.let { probe(it.host!!, if (it.port > 0) it.port else if (it.scheme == "https") 443 else 80) } }
            sshOk = ssh.await(); panelOk = web.await()
            delay(5000)
        }
    }

    fun open(uri: String, missing: String) {
        if (!openIfPossible(context, uri)) notice = missing else app.haptics.light()
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        Section("(01)", "Быстрые действия", Modifier.padding(top = 8.dp)) { Txt(server.pingHost, T.index) }
        Card {
            ActionRow("SSH", "${server.sshUser}@${server.pingHost}" + if (server.sshPort != 22) ":${server.sshPort}" else "", leading = { Dot(sshOk) }) {
                open(sshUri, "Нет SSH-клиента: поставьте Termius или JuiceSSH — они открывают ссылки ssh://")
            }
            if (hasSftp) ActionRow("SFTP", "файлы", leading = { Dot(sshOk) }) { open(sftpUri, "Нет приложения для sftp://") }
            if (panelUri != null) ActionRow("Панель", panelUri.authority, leading = { Dot(panelOk) }) { open(panelUri.toString(), "Нет браузера") }
        }
        Txt(
            notice ?: if (up) "Точка — открыт ли порт через туннель, проверка раз в 5 секунд." else "Туннель выключен: адреса внутри VPN сейчас недоступны.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp), color = if (notice != null) C.Amber else C.Hint,
        )

        Section("(02)", "Адреса", Modifier.padding(top = 12.dp))
        Card {
            InlineField("Пользователь SSH", user, { user = it }, placeholder = "root", onDone = ::save, onFocusLost = ::save)
            InlineField("Порт SSH", port, { port = it.filter(Char::isDigit).take(5) }, placeholder = "22", keyboard = KeyboardType.Number, onDone = ::save, onFocusLost = ::save)
            InlineField("Панель", panel, { panel = it }, placeholder = "http://10.8.0.1:8791", keyboard = KeyboardType.Uri, onDone = ::save, onFocusLost = ::save)
        }
        Txt("Сохраняется само. SSH и SFTP идут на IP сервера внутри туннеля (${server.pingHost}).", T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp))
    }
}

@Composable
private fun Dot(ok: Boolean?) {
    val color by animateColorAsState(when (ok) { true -> C.Green; false -> C.Red; null -> C.Faint }, tween(300), label = "dot")
    Box(Modifier.size(7.dp).clip(CircleShape).background(color))
    Spacer(Modifier.width(12.dp))
}

/** Порт открыт: TCP-соединение за 1,5 с. Только соединение, без данных. */
private fun probe(host: String, port: Int): Boolean = try {
    Socket().use { it.connect(InetSocketAddress(host, port), 1500); true }
} catch (_: Exception) {
    false
}

private fun canOpen(context: Context, uri: String) =
    Intent(Intent.ACTION_VIEW, Uri.parse(uri)).resolveActivity(context.packageManager) != null

private fun openIfPossible(context: Context, uri: String): Boolean = try {
    context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(uri)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    true
} catch (_: Exception) {
    false
}

