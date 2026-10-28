package ru.lkxa.wgswitch.ui

import android.content.Intent
import android.provider.Settings as AndroidSettings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.BuildConfig
import ru.lkxa.wgswitch.data.ConfigText
import ru.lkxa.wgswitch.data.PeerInfo
import ru.lkxa.wgswitch.data.Settings

const val REPO_URL = "https://github.com/lolkekxa/WgSwitch"

@Composable
fun SettingsScreen(app: App, nav: Nav, settings: Settings, askNotifications: () -> Unit) {
    val context = currentContext()
    val server = settings.selectedServer
    var error by remember { mutableStateOf<String?>(null) }
    var name by remember(server?.name) { mutableStateOf(server?.name ?: "") }
    var host by remember(server?.name) { mutableStateOf(server?.pingHost ?: "") }
    var confirmDelete by remember { mutableStateOf(false) }
    LaunchedEffect(confirmDelete) { if (confirmDelete) { delay(3000); confirmDelete = false } }

    val restorePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) readLimited(context, uri, 1 shl 20)?.let { nav.push(Screen.Restore(it)) } ?: run { error = "Не удалось прочитать файл" }
    }

    fun saveName() {
        val s = server ?: return
        val n = name.trim()
        if (n == s.name) return
        when {
            !ConfigText.isValidName(n) -> { error = "Имя: латиница, цифры и _=+.- , до 15 символов"; name = s.name }
            else -> try {
                if (app.tunnels.isUp(s.name)) app.tunnels.forget(s.name)
                app.store.rename(s.name, n)
                error = null
            } catch (e: Exception) {
                error = e.message
                name = s.name
            }
        }
    }

    fun saveHost() {
        val s = server ?: return
        val h = host.trim()
        if (h == s.pingHost) return
        if (!ConfigText.isValidHost(h)) { error = "IP для проверки: например 10.8.0.1"; host = s.pingHost; return }
        app.store.updateServer(s.name) { it.copy(pingHost = h) }
        error = null
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        if (server != null) {
            Section("(01)", "Сервер", Modifier.padding(top = 8.dp)) {
                TextButton("+ Добавить", { nav.push(Screen.Add) })
                Spacer(Modifier.width(16.dp))
                TextButton(if (confirmDelete) "Точно удалить?" else "Удалить", {
                    if (!confirmDelete) { confirmDelete = true; app.haptics.light(); return@TextButton }
                    app.tunnels.forget(server.name)
                    app.store.removeServer(server.name)
                    app.haptics.off()
                    confirmDelete = false
                    if (app.store.current.servers.isEmpty()) nav.reset(Screen.Welcome)
                }, color = if (confirmDelete) C.Red else C.Muted)
            }
            Card {
                InlineField("Имя", name, { name = it }, onDone = ::saveName, onFocusLost = ::saveName)
                InlineField("IP для проверки", host, { host = it }, placeholder = ConfigText.DEFAULT_HOST,
                    keyboard = KeyboardType.Uri, onDone = ::saveHost, onFocusLost = ::saveHost)
            }
            Txt(error ?: "", T.hint, Modifier.padding(horizontal = 4.dp, vertical = 2.dp), color = C.Red)

            val peer = remember(server.name) { runCatching { PeerInfo.of(app.store.readConfig(server.name)) }.getOrNull() }
            if (peer != null) ServerSteps(app, server.name, peer, "(02)")
        }

        Section("(03)", "Поведение", Modifier.padding(top = 16.dp))
        Card {
            ToggleRow("Вибрация", settings.haptics, { on ->
                app.store.update { it.copy(haptics = on) }
                app.haptics.enabled = on
                app.haptics.light()
            }, hint = "щелчок, «дыхание» при подключении")
            ToggleRow("Подробно на главной", settings.showDetails, { on ->
                app.haptics.light()
                app.store.update { it.copy(showDetails = on) }
            }, hint = "ещё handshake и трафик")
            ToggleRow("Уведомление в шторке", settings.notify, { on ->
                app.haptics.light()
                app.store.update { it.copy(notify = on) }
                if (on) askNotifications()
            }, hint = "с кнопками; закрывается свайпом")
            ActionRow("Постоянный VPN", "Android") {
                try { context.startActivity(Intent(AndroidSettings.ACTION_VPN_SETTINGS)) } catch (_: Exception) { }
            }
        }
        Txt("Постоянный VPN включается в настройках Android: WgSwitch поднимет выбранный сервер сам после перезагрузки.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp))

        Section("(04)", "Резервная копия", Modifier.padding(top = 12.dp))
        Card {
            ActionRow("Сохранить копию", ".wgsb") { nav.push(Screen.Export) }
            ActionRow("Восстановить из копии") { restorePicker.launch(arrayOf("*/*")) }
        }
        Txt("Тот же файл, что у WgSwitch на ПК: можно перенести серверы в обе стороны.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp))

        Spacer(Modifier.height(24.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            MadeBy { openUrl(context, "https://lkxa.ru") }
            Spacer(Modifier.weight(1f))
            Txt("WgSwitch ${BuildConfig.VERSION_NAME}", T.value, Modifier.press({ openUrl(context, REPO_URL) }), color = C.Muted)
        }
    }
}

fun readLimited(context: android.content.Context, uri: android.net.Uri, limit: Int): ByteArray? = try {
    context.contentResolver.openInputStream(uri)?.use { input ->
        val bytes = input.readNBytesCompat(limit + 1)
        if (bytes.size > limit) null else bytes
    }
} catch (_: Exception) {
    null
}

private fun java.io.InputStream.readNBytesCompat(n: Int): ByteArray {
    val out = java.io.ByteArrayOutputStream()
    val buf = ByteArray(8192)
    while (out.size() < n) {
        val r = read(buf, 0, minOf(buf.size, n - out.size()))
        if (r < 0) break
        out.write(buf, 0, r)
    }
    return out.toByteArray()
}
