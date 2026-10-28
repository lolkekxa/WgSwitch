package ru.lkxa.wgswitch.ui

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import org.amnezia.awg.config.Config
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.data.Backup
import ru.lkxa.wgswitch.data.BackupException
import ru.lkxa.wgswitch.data.BackupPayload
import ru.lkxa.wgswitch.data.BackupServer
import ru.lkxa.wgswitch.data.ConfigText
import ru.lkxa.wgswitch.data.PeerInfo
import ru.lkxa.wgswitch.data.Server

/** Добавить сервер: QR-код, файл .conf или резервная копия. При первом запуске — с заголовком. */
@Composable
fun AddScreen(app: App, nav: Nav, welcome: Boolean) {
    val context = currentContext()
    var error by remember { mutableStateOf<String?>(null) }

    val confPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        val text = readLimited(context, uri, 64 * 1024)?.toString(Charsets.UTF_8)
        if (text == null || !text.contains("[Interface]")) error = "Это не конфиг WireGuard"
        else nav.push(Screen.NewServer(text, ConfigText.meta(text).first ?: ConfigText.nameFrom(displayName(context, uri))))
    }
    val backupPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        readLimited(context, uri, 1 shl 20)?.let { nav.push(Screen.Restore(it)) } ?: run { error = "Не удалось прочитать файл" }
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        if (welcome) {
            Spacer(Modifier.height(32.dp))
            Txt("Добавьте сервер", T.heading, Modifier.padding(horizontal = 4.dp))
            Txt(
                "Нужен конфиг WireGuard: QR-код, файл .conf или резервная копия из WgSwitch на ПК.",
                T.body, Modifier.padding(start = 4.dp, end = 4.dp, top = 8.dp, bottom = 16.dp),
            )
        }
        Section("(01)", "Откуда взять", Modifier.padding(top = 8.dp))
        Card {
            ActionRow("Сканировать QR-код") {
                error = null
                val options = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).enableAutoZoom().build()
                GmsBarcodeScanning.getClient(context, options).startScan()
                    .addOnSuccessListener { code ->
                        val text = code.rawValue
                        if (text == null || !text.contains("[Interface]")) error = "В QR-коде не конфиг WireGuard"
                        else { app.haptics.success(); nav.push(Screen.NewServer(text, ConfigText.meta(text).first ?: "wg0")) }
                    }
                    .addOnFailureListener { error = "Сканер недоступен без сервисов Google — добавьте файл .conf" }
            }
            ActionRow("Файл .conf") { error = null; confPicker.launch(arrayOf("*/*")) }
            ActionRow("Резервная копия", ".wgsb") { error = null; backupPicker.launch(arrayOf("*/*")) }
        }
        Txt(
            error ?: "На ПК: WgSwitch → Настройки → Резервная копия → Экспорт. Пароль тот же.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp), color = if (error != null) C.Red else C.Hint,
        )
    }
}

/** Новый сервер из QR или файла: имя и IP для проверки, конфиг проверяется движком WireGuard. */
@Composable
fun NewServerScreen(app: App, nav: Nav, screen: Screen.NewServer) {
    var name by remember { mutableStateOf(screen.name) }
    var host by remember { mutableStateOf(ConfigText.pingHostFor(screen.config)) }
    var error by remember { mutableStateOf<String?>(null) }
    val endpoint = remember { Regex("""(?im)^\s*Endpoint\s*=\s*(\S+)""").find(screen.config)?.groupValues?.get(1) ?: "—" }

    fun add() {
        val n = name.trim()
        val h = host.trim()
        error = when {
            !ConfigText.isValidName(n) -> "Имя: латиница, цифры и _=+.- , до 15 символов"
            !ConfigText.isValidHost(h) -> "IP для проверки: например 10.8.0.1"
            else -> try {
                Config.parse(screen.config.byteInputStream())
                null
            } catch (e: Exception) {
                "Конфиг не читается: ${e.message ?: "ошибка разбора"}"
            }
        }
        if (error != null) return
        app.store.putServer(Server(n, h), screen.config)
        app.haptics.success()
        nav.reset(Screen.Main)
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        Section("(01)", "Сервер", Modifier.padding(top = 8.dp))
        Card {
            InlineField("Имя", name, { name = it }, placeholder = "wg0")
            InlineField("IP для проверки", host, { host = it }, placeholder = ConfigText.DEFAULT_HOST, keyboard = KeyboardType.Uri)
            InfoRow("Endpoint", endpoint, valueColor = C.Muted)
        }
        Txt(
            error ?: "IP для проверки — адрес сервера внутри туннеля: по нему WgSwitch понимает, что сервер жив.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp), color = if (error != null) C.Red else C.Hint,
        )
        // без этого шага сервер не пустит телефон: «нет ответа» навсегда
        val peer = remember { PeerInfo.of(screen.config) }
        if (peer != null) ServerSteps(app, name.trim().ifEmpty { "wg0" }, peer, "(02)")
        Spacer(Modifier.height(16.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton("Назад", { nav.pop() })
            Spacer(Modifier.weight(1f))
            Pill("Добавить", ::add)
        }
    }
}

/** Восстановление из .wgsb: расшифровка занимает пару секунд — 600 000 итераций PBKDF2, как на ПК. */
@Composable
fun RestoreScreen(app: App, nav: Nav, screen: Screen.Restore) {
    var password by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    fun restore() {
        if (busy || password.isEmpty()) return
        busy = true
        error = null
        scope.launch {
            try {
                val count = withContext(Dispatchers.Default) {
                    val payload = Backup.read(screen.data, password)
                    for (s in payload.servers) {
                        if (!ConfigText.isValidName(s.name) || s.config.isBlank()) throw BackupException("В копии повреждён сервер «${s.name}»")
                    }
                    for (s in payload.servers) {
                        if (app.tunnels.isUp(s.name)) app.tunnels.forget(s.name)
                        app.store.putServer(Server(s.name, s.pingHost, s.sshUser, s.sshPort, s.panelUrl), s.config)
                    }
                    payload.selected?.let { sel -> if (app.store.current.find(sel) != null) app.store.update { it.copy(selected = sel) } }
                    payload.servers.size
                }
                app.haptics.success()
                nav.reset(if (count > 0) Screen.Main else Screen.Welcome)
            } catch (e: BackupException) {
                app.haptics.error()
                error = e.message
            } catch (e: Exception) {
                app.haptics.error()
                error = "Не удалось восстановить: ${e.message}"
            } finally {
                busy = false
            }
        }
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        Section("(01)", "Пароль копии", Modifier.padding(top = 8.dp))
        Card {
            InlineField("Пароль", password, { password = it }, password = true, onDone = ::restore)
        }
        Txt(
            error ?: if (busy) "Расшифровка…" else "Серверы с теми же именами заменятся, остальные останутся.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp), color = if (error != null) C.Red else C.Hint,
        )
        Spacer(Modifier.height(16.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton("Назад", { nav.pop() })
            Spacer(Modifier.weight(1f))
            Pill(if (busy) "Расшифровка…" else "Восстановить", ::restore, enabled = !busy && password.isNotEmpty())
        }
    }
}

/** Сохранить копию: пароль дважды, файл — куда выберет пользователь (Загрузки, Диск…). */
@Composable
fun ExportScreen(app: App, nav: Nav) {
    val context = currentContext()
    var pass by remember { mutableStateOf("") }
    var pass2 by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var done by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val saver = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            try {
                withContext(Dispatchers.Default) {
                    val s = app.store.current
                    val payload = BackupPayload(s.selected, s.servers.map {
                        BackupServer(it.name, it.pingHost, app.store.readConfig(it.name), it.sshUser, it.sshPort, it.panelUrl)
                    })
                    val bytes = Backup.write(payload, pass)
                    context.contentResolver.openOutputStream(uri, "wt")!!.use { out -> out.write(bytes) }
                }
                app.haptics.success()
                done = "Сохранено: ${displayName(context, uri) ?: "файл"}"
                pass = ""; pass2 = ""
            } catch (e: Exception) {
                app.haptics.error()
                error = "Не удалось сохранить: ${e.message}"
            } finally {
                busy = false
            }
        }
    }

    fun save() {
        done = null
        error = when {
            pass.length < Backup.MIN_PASSWORD -> "Пароль — от ${Backup.MIN_PASSWORD} символов"
            pass != pass2 -> "Пароли не совпадают"
            else -> null
        }
        if (error == null) saver.launch(Backup.defaultFileName())
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        Section("(01)", "Пароль для копии", Modifier.padding(top = 8.dp))
        Card {
            InlineField("Пароль", pass, { pass = it }, password = true)
            InlineField("Ещё раз", pass2, { pass2 = it }, password = true, onDone = ::save)
        }
        Txt(
            error ?: done ?: if (busy) "Шифрование…" else "Файл содержит ключи всех серверов, зашифрован паролем. Без пароля его не открыть — и не восстановить.",
            T.hint, Modifier.padding(horizontal = 4.dp, vertical = 8.dp),
            color = when { error != null -> C.Red; done != null -> C.Green; else -> C.Hint },
        )
        Spacer(Modifier.height(16.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton("Назад", { nav.pop() })
            Spacer(Modifier.weight(1f))
            Pill(if (busy) "Шифрование…" else "Сохранить", ::save, enabled = !busy)
        }
        Spacer(Modifier.width(1.dp))
    }
}

fun displayName(context: Context, uri: Uri): String? = try {
    context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
        if (c.moveToFirst()) c.getString(0) else null
    }
} catch (_: Exception) {
    null
}
