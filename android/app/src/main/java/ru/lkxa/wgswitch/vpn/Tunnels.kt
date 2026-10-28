package ru.lkxa.wgswitch.vpn

import android.content.Context
import org.amnezia.awg.backend.BackendException
import org.amnezia.awg.backend.GoBackend
import org.amnezia.awg.backend.Tunnel
import org.amnezia.awg.config.BadConfigException
import org.amnezia.awg.config.Config
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import ru.lkxa.wgswitch.data.Store

enum class State { Off, Connecting, Connected, NoResponse }

/** Состояние туннеля. На Android одновременно работает только один VPN, поэтому и статус один. */
data class Status(
    val name: String? = null,
    val state: State = State.Off,
    val disconnecting: Boolean = false,
    val pingMs: Int? = null,
    val since: Long? = null,
    val handshake: Long? = null,
    val rx: Long? = null,
    val tx: Long? = null,
    val error: String? = null,
) {
    val up get() = state == State.Connected || state == State.NoResponse
}

/**
 * Туннели через встроенный движок WireGuard (тот же wireguard-go, что в официальном приложении).
 * Статус честный, как на ПК: «Подключено» — только когда сервер внутри туннеля отвечает на пинг.
 */
class Tunnels(context: Context, private val store: Store) {
    private val backend by lazy { GoBackend(context.applicationContext) }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Mutex()

    private val _status = MutableStateFlow(Status())
    val status: StateFlow<Status> = _status.asStateFlow()

    /** Сервер ответил на пинг — для «пульса» кнопки. */
    private val _heartbeat = MutableSharedFlow<Unit>(extraBufferCapacity = 1)
    val heartbeat: SharedFlow<Unit> = _heartbeat

    private var active: Tun? = null
    private var poll: Job? = null

    private inner class Tun(private val tunnelName: String) : Tunnel {
        override fun getName() = tunnelName
        override fun onStateChange(newState: Tunnel.State) {
            // VPN могли выключить снаружи: другой VPN, системные настройки, «Отключить» в шторке
            if (newState == Tunnel.State.DOWN) scope.launch { lock.withLock { if (active === this@Tun) dropped() } }
        }
    }

    fun isUp(name: String?) = name != null && _status.value.name.equals(name, true) && _status.value.state != State.Off

    fun toggle(name: String) = scope.launch { if (isUp(name)) down() else up(name) }

    fun connect(name: String) = scope.launch { up(name) }

    fun disconnect() = scope.launch { down() }

    private suspend fun up(name: String) = lock.withLock {
        val server = store.current.find(name) ?: return@withLock
        _status.value = Status(name, State.Connecting)
        try {
            val config = store.configFile(name).inputStream().use { Config.parse(it) }
            val t = Tun(server.name)
            active = t
            // если работал другой туннель, GoBackend сначала опустит его
            backend.setState(t, Tunnel.State.UP, config)
            _status.value = Status(name, State.Connecting, since = System.currentTimeMillis())
            startPolling(t, server.pingHost)
        } catch (e: Exception) {
            active = null
            poll?.cancel()
            _status.value = Status(name, State.Off, error = describe(e))
        }
    }

    private suspend fun down() = lock.withLock {
        val t = active ?: return@withLock
        _status.value = _status.value.copy(disconnecting = true)
        poll?.cancel()
        try {
            backend.setState(t, Tunnel.State.DOWN, null)
        } catch (_: Exception) {
            // уже опущен
        }
        active = null
        _status.value = Status(t.getName(), State.Off)
    }

    private fun dropped() {
        val name = active?.getName()
        active = null
        poll?.cancel()
        _status.value = Status(name, State.Off, error = "VPN выключен системой или другим приложением")
    }

    /** Если выбранный сервер переименовали или удалили, пока туннель работал. */
    fun forget(name: String) = scope.launch { if (isUp(name)) down() }

    private fun startPolling(t: Tun, host: String) {
        poll?.cancel()
        poll = scope.launch {
            var fails = 0
            val started = System.currentTimeMillis()
            var lastOk = started
            while (isActive) {
                // сервер молчит дольше 20 с — сеанс «залип» (сервер перезапускали, меняли ключ, сеть сменилась):
                // поднимаем туннель заново — новый порт и свежий handshake. Не чаще раза в минуту.
                val now = System.currentTimeMillis()
                if (now - lastOk > SILENT_RESTART && now - lastRestart > RESTART_COOLDOWN) {
                    restart(t, host)
                    return@launch
                }
                // адрес берём из настроек каждый раз: его могли поправить, пока туннель работает
                val ping = Ping.ms(store.current.find(t.getName())?.pingHost ?: host)
                val stats = try { backend.getStatistics(t) } catch (_: Exception) { null }
                val ok = lock.withLock {
                    if (active !== t) return@launch
                    val hs = stats?.peers()?.maxOfOrNull { stats.peer(it)?.latestHandshakeEpochMillis() ?: 0L }?.takeIf { it > 0 }
                    // ответ засчитывается только при живом handshake: в первые доли секунды маршруты VPN ещё не встали,
                    // и пинг может уйти мимо туннеля — если адрес сервера доступен и так, это было бы ложное «Подключено»
                    val answered = ping != null && hs != null
                    if (answered) fails = 0 else fails++
                    val cur = _status.value
                    val state = when {
                        answered -> State.Connected
                        // пару раз не ответил и прошло больше 6 с — сервер молчит, а не просто ещё не успел
                        fails >= 2 && System.currentTimeMillis() - started > 6000 -> State.NoResponse
                        else -> cur.state
                    }
                    _status.value = cur.copy(state = state, pingMs = ping?.takeIf { answered }, handshake = hs, rx = stats?.totalRx(), tx = stats?.totalTx(), error = null)
                    answered
                }
                if (ok) {
                    lastOk = System.currentTimeMillis()
                    _heartbeat.tryEmit(Unit)
                }
                delay(if (ok) 3000 else 1500)
            }
        }
    }

    private var lastRestart = 0L

    /** Тихий перезапуск: статус и «В сети» не сбрасываются, кнопка не мигает «Выключено». */
    private suspend fun restart(old: Tun, host: String) = lock.withLock {
        if (active !== old) return@withLock
        val name = old.getName()
        lastRestart = System.currentTimeMillis()
        try {
            val config = store.configFile(name).inputStream().use { Config.parse(it) }
            val t = Tun(name)
            active = t // старый получит DOWN, но он уже не активный — dropped() не сработает
            backend.setState(old, Tunnel.State.DOWN, null)
            backend.setState(t, Tunnel.State.UP, config)
            startPolling(t, host)
        } catch (e: Exception) {
            active = null
            _status.value = Status(name, State.Off, error = describe(e))
        }
    }

    private fun describe(e: Exception): String = when (e) {
        is BackendException -> when (e.reason) {
            BackendException.Reason.VPN_NOT_AUTHORIZED -> "Нет разрешения на VPN"
            BackendException.Reason.UNABLE_TO_START_VPN -> "Android не дал запустить VPN"
            BackendException.Reason.TUN_CREATION_ERROR -> "Не удалось создать туннель — возможно, работает другой VPN"
            BackendException.Reason.DNS_RESOLUTION_FAILURE -> "Не удалось найти адрес сервера (Endpoint)"
            BackendException.Reason.GO_ACTIVATION_ERROR_CODE -> "Ошибка WireGuard (код ${e.format.firstOrNull() ?: "?"})"
            else -> "Ошибка WireGuard: ${e.reason}"
        }
        is BadConfigException -> "Конфиг повреждён: ${e.message ?: "ошибка разбора"}"
        is java.io.IOException -> "Не удалось прочитать конфиг"
        else -> e.message ?: e.javaClass.simpleName
    }

    companion object {
        private const val SILENT_RESTART = 20_000L
        private const val RESTART_COOLDOWN = 60_000L

        /** «Постоянный VPN» Android поднимает сервис сам — тогда включаем выбранный сервер. */
        fun registerAlwaysOn(tunnels: Tunnels, store: Store) {
            GoBackend.setAlwaysOnCallback {
                store.current.selectedServer?.let { tunnels.connect(it.name) }
            }
        }
    }
}
