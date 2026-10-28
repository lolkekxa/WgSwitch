package ru.lkxa.wgswitch.vpn

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.widget.RemoteViews
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.MainActivity
import ru.lkxa.wgswitch.R
import ru.lkxa.wgswitch.data.Settings

/**
 * Уведомление в шторке и виджеты на главном экране: оба показывают выбранный сервер и управляют им.
 * Уведомление закрывается свайпом и не возвращается, пока не сменится состояние туннеля.
 */
class StatusUi(private val app: App) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val notifications = app.getSystemService(NotificationManager::class.java)

    private var dismissed = false
    private var lastKey: String? = null        // имя + состояние: смена — повод показать уведомление снова
    private var lastWidget = 0L
    private var lastWidgetKey: String? = null

    fun start() {
        notifications.createNotificationChannel(
            NotificationChannel(CHANNEL, "Состояние VPN", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Сервер, состояние и кнопки включения. Закрывается свайпом."
                setShowBadge(false)
            },
        )
        scope.launch {
            app.store.settings.combine(app.tunnels.status) { s, st -> s to st }.collect { (s, st) -> render(s, st) }
        }
    }

    /** Перерисовать всё: выдали разрешение на уведомления, добавили виджет. */
    fun refresh() = render(app.store.current, app.tunnels.status.value, forceWidget = true)

    fun onDismissed() {
        dismissed = true
    }

    private fun render(s: Settings, live: Status, forceWidget: Boolean = false) {
        val server = s.selectedServer
        val st = if (server != null && live.name.equals(server.name, true)) live else Status(server?.name)
        val view = View.of(st)

        val key = "${server?.name}|${view.text}"
        if (key != lastKey) {
            lastKey = key
            dismissed = false
        }
        renderNotification(s, st, view)
        // виджет: состояние — сразу, пинг — не чаще раза в 15 с, чтобы не будить лаунчер каждые 3 секунды
        val now = System.currentTimeMillis()
        if (forceWidget || key != lastWidgetKey || now - lastWidget > 15_000) {
            lastWidget = now
            lastWidgetKey = key
            renderWidgets(s, st, view)
        }
    }

    private fun renderNotification(s: Settings, st: Status, view: View) {
        val server = s.selectedServer
        if (!s.notify || server == null || dismissed || !canNotify()) {
            if (!s.notify || server == null) notifications.cancel(ID)
            return
        }
        val toggle = Notification.Action.Builder(
            null, if (st.state == State.Off) "Включить" else "Выключить", action(ACTION_TOGGLE),
        ).build()
        val b = Notification.Builder(app, CHANNEL)
            .setSmallIcon(R.drawable.ic_tile)
            .setColor(view.color)
            .setContentTitle(server.name)
            .setContentText(view.text + (st.pingMs?.takeIf { st.state == State.Connected }?.let { " · $it мс" } ?: ""))
            .setContentIntent(openApp())
            .setDeleteIntent(action(ACTION_DISMISSED))
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setCategory(Notification.CATEGORY_STATUS)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .addAction(toggle)
        next(s)?.let { b.addAction(Notification.Action.Builder(null, "→ $it", action(ACTION_NEXT)).build()) }
        notifications.notify(ID, b.build())
    }

    private fun renderWidgets(s: Settings, st: Status, view: View) {
        val manager = AppWidgetManager.getInstance(app)
        val ids = manager.getAppWidgetIds(ComponentName(app, WgWidget::class.java))
        if (ids.isEmpty()) return
        val rv = RemoteViews(app.packageName, R.layout.widget)
        val server = s.selectedServer
        rv.setTextViewText(R.id.widget_name, server?.name ?: "WgSwitch")
        rv.setTextViewText(
            R.id.widget_state,
            if (server == null) "Добавьте сервер"
            else view.text + (st.pingMs?.takeIf { st.state == State.Connected }?.let { " · $it мс" } ?: ""),
        )
        rv.setTextColor(R.id.widget_state, if (st.state == State.Off) GREY else view.color)
        rv.setInt(R.id.widget_ring, "setColorFilter", if (st.state == State.Off) LINE else view.color)
        rv.setInt(R.id.widget_icon, "setColorFilter", view.color)
        rv.setOnClickPendingIntent(R.id.widget_power, action(ACTION_TOGGLE))
        rv.setOnClickPendingIntent(R.id.widget_root, openApp())
        manager.updateAppWidget(ids, rv)
    }

    private fun next(s: Settings): String? {
        if (s.servers.size < 2) return null
        val i = s.servers.indexOfFirst { it.name.equals(s.selectedServer?.name, true) }
        return s.servers[(i + 1) % s.servers.size].name
    }

    private fun canNotify() = Build.VERSION.SDK_INT < 33 ||
        app.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun openApp() = PendingIntent.getActivity(
        app, 0, Intent(app, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun action(name: String) = PendingIntent.getBroadcast(
        app, name.hashCode(), Intent(app, Actions::class.java).setAction(name),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** Подпись и цвет — те же, что на главном экране. */
    private data class View(val text: String, val color: Int) {
        companion object {
            fun of(st: Status) = when {
                st.disconnecting -> View("Отключение", AMBER)
                st.state == State.Connected -> View("Подключено", GREEN)
                st.state == State.NoResponse -> View("Нет ответа от сервера", RED)
                st.state == State.Connecting -> View("Подключение", AMBER)
                else -> View("Выключено", GREY)
            }
        }
    }

    companion object {
        private const val CHANNEL = "status"
        private const val ID = 1
        const val ACTION_TOGGLE = "ru.lkxa.wgswitch.TOGGLE"
        const val ACTION_NEXT = "ru.lkxa.wgswitch.NEXT"
        const val ACTION_DISMISSED = "ru.lkxa.wgswitch.DISMISSED"
        private const val GREEN = 0xFF22C55E.toInt()
        private const val RED = 0xFFEF4444.toInt()
        private const val AMBER = 0xFFF59E0B.toInt()
        private const val GREY = 0xFF7A7A7A.toInt()
        private const val LINE = 0xFF2A2A2A.toInt()
    }
}

/** Кнопки уведомления и виджета. */
class Actions : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val app = App.of(context)
        val s = app.store.current
        when (intent.action) {
            StatusUi.ACTION_DISMISSED -> app.statusUi.onDismissed()
            StatusUi.ACTION_TOGGLE -> {
                val server = s.selectedServer ?: return
                if (app.tunnels.isUp(server.name)) app.tunnels.disconnect()
                else connectOrAsk(context, app, server.name)
            }
            StatusUi.ACTION_NEXT -> {
                if (s.servers.size < 2) return
                val i = s.servers.indexOfFirst { it.name.equals(s.selectedServer?.name, true) }
                val next = s.servers[(i + 1) % s.servers.size].name
                val wasUp = app.tunnels.status.value.state != State.Off
                app.store.update { it.copy(selected = next) }
                // туннель работал — переключаемся на следующий сервер сразу
                if (wasUp) connectOrAsk(context, app, next)
            }
        }
    }

    private fun connectOrAsk(context: Context, app: App, name: String) {
        if (VpnService.prepare(context) == null) app.tunnels.connect(name)
        else context.startActivity(
            Intent(context, MainActivity::class.java).setAction(MainActivity.ACTION_CONNECT).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }
}

/** Виджет на главном экране: кнопка питания с кольцом состояния, имя сервера, состояние. */
class WgWidget : android.appwidget.AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = App.of(context).statusUi.refresh()
}
