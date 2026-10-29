package ru.lkxa.wgswitch.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.vpn.State

/** Экраны. depth — для направления сдвига: глубже — въезжает справа, назад — слева. */
sealed class Screen(val depth: Int, val title: String) {
    data object Main : Screen(0, "WgSwitch")
    data object Welcome : Screen(0, "WgSwitch")
    data object Settings : Screen(1, "Настройки")
    data object Tools : Screen(1, "Сервер")
    data object Add : Screen(2, "Добавить сервер")
    data object Export : Screen(2, "Резервная копия")
    class NewServer(val config: String, val name: String) : Screen(3, "Новый сервер")
    class Restore(val data: ByteArray) : Screen(3, "Восстановление")
}

/** Навигация: стек экранов и «назад» жестом. */
class Nav(initial: Screen) {
    var stack by mutableStateOf(listOf(initial))
        private set
    val top get() = stack.last()
    fun push(s: Screen) { stack = stack + s }
    fun pop() { if (stack.size > 1) stack = stack.dropLast(1) }
    fun reset(s: Screen) { stack = listOf(s) }
}

@Composable
fun WgSwitchApp(app: App, withVpn: (() -> Unit) -> Unit, vpnDenied: MutableState<Int>, askNotifications: () -> Unit) {
    val settings by app.store.settings.collectAsState()
    val status by app.tunnels.status.collectAsState()
    app.haptics.enabled = settings.haptics
    val nav = remember { Nav(if (settings.servers.isEmpty()) Screen.Welcome else Screen.Main) }
    var notice by remember { mutableStateOf<String?>(null) }

    // все серверы удалены — обратно к добавлению
    LaunchedEffect(settings.servers.isEmpty()) {
        if (settings.servers.isEmpty() && nav.top is Screen.Main) nav.reset(Screen.Welcome)
    }
    LaunchedEffect(vpnDenied.value) {
        if (vpnDenied.value > 0) notice = "Без разрешения на VPN туннель не включить"
    }

    BackHandler(nav.stack.size > 1) { nav.pop() }

    val selected = settings.selectedServer
    val mine = status.name.equals(selected?.name, true)
    val markColor by animateColorAsState(
        when {
            !mine -> C.Muted
            status.disconnecting || status.state == State.Connecting -> C.Amber
            status.state == State.Connected -> C.Green
            status.state == State.NoResponse -> C.Red
            else -> C.Muted
        }, tween(300), label = "mark",
    )

    Column(Modifier.fillMaxSize().background(C.Bg).systemBarsPadding().imePadding()) {
        Header(
            title = if (nav.top is Screen.Tools) selected?.name ?: "Сервер" else nav.top.title,
            mark = markColor,
            gear = nav.top is Screen.Main || nav.top is Screen.Settings || nav.top is Screen.Tools,
            gearOpen = nav.top is Screen.Settings,
            onGear = {
                app.haptics.light()
                when (nav.top) {
                    Screen.Settings -> nav.pop()
                    Screen.Tools -> { nav.pop(); nav.push(Screen.Settings) }
                    else -> nav.push(Screen.Settings)
                }
            },
            toolsOpen = nav.top is Screen.Tools,
            onTools = {
                app.haptics.light()
                when (nav.top) {
                    Screen.Tools -> nav.pop()
                    Screen.Settings -> { nav.pop(); nav.push(Screen.Tools) }
                    else -> nav.push(Screen.Tools)
                }
            },
        )
        val density = LocalDensity.current
        AnimatedContent(
            targetState = nav.top,
            transitionSpec = {
                val dir = if (targetState.depth >= initialState.depth) 1 else -1
                val inPx = with(density) { 40.dp.roundToPx() } * dir
                val outPx = with(density) { -24.dp.roundToPx() } * dir
                (slideInHorizontally(tween(500, easing = Expo)) { inPx } + fadeIn(tween(400, easing = Expo)))
                    .togetherWith(slideOutHorizontally(tween(500, easing = Expo)) { outPx } + fadeOut(tween(250)))
            },
            modifier = Modifier.fillMaxSize(),
            label = "screens",
        ) { screen ->
            Box(Modifier.fillMaxSize().padding(horizontal = 20.dp)) {
                when (screen) {
                    Screen.Main -> MainScreen(app, settings, status, withVpn, notice) { notice = null }
                    Screen.Welcome -> AddScreen(app, nav, welcome = true)
                    Screen.Tools -> ToolsScreen(app, settings)
                    Screen.Settings -> SettingsScreen(app, nav, settings, askNotifications)
                    Screen.Add -> AddScreen(app, nav, welcome = false)
                    Screen.Export -> ExportScreen(app, nav)
                    is Screen.NewServer -> NewServerScreen(app, nav, screen)
                    is Screen.Restore -> RestoreScreen(app, nav, screen)
                }
            }
        }
    }
}

@Composable
private fun Header(
    title: String, mark: androidx.compose.ui.graphics.Color, gear: Boolean, gearOpen: Boolean, onGear: () -> Unit,
    toolsOpen: Boolean, onTools: () -> Unit,
) {
    Row(Modifier.fillMaxWidth().height(60.dp).padding(start = 20.dp, end = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Mark(mark)
        Spacer(Modifier.width(12.dp))
        Rolling(title, T.title, Modifier.weight(1f))
        // панель сервера: SSH, SFTP, веб-панель — видна там же, где шестерёнка
        val toolsTint by animateColorAsState(if (toolsOpen) C.Fg else C.Muted, tween(250), label = "toolsTint")
        if (gear) Box(Modifier.size(44.dp).clip(CircleShape).press(onTools), contentAlignment = Alignment.Center) {
            Glyph(SERVER, toolsTint, size = 20.dp)
        }
        val turn by animateFloatAsState(if (gearOpen) 90f else 0f, tween(700, easing = Expo), label = "gear")
        val tint by animateColorAsState(if (gearOpen) C.Fg else C.Muted, tween(250), label = "gearTint")
        val alpha by animateFloatAsState(if (gear) 1f else 0f, tween(250), label = "gearAlpha")
        if (alpha > 0f) Box(
            Modifier.size(44.dp).clip(CircleShape).press(onGear, enabled = gear),
            contentAlignment = Alignment.Center,
        ) {
            Glyph(GEAR, tint.copy(alpha = alpha), Modifier.rotate(turn), size = 20.dp)
        }
    }
}

const val SERVER = "M4 2h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M4 14h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z M6 6h.01 M6 18h.01"

const val GEAR = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0z"

fun copy(context: Context, text: String) {
    (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("WgSwitch", text))
}

fun openUrl(context: Context, url: String) {
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: Exception) {
        // нет браузера — молча
    }
}

@Composable
fun currentContext(): Context = LocalContext.current
