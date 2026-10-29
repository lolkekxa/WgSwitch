package ru.lkxa.wgswitch.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** Сервер: конфиг лежит в закрытой папке приложения под его именем, здесь — то, что вокруг него. */
data class Server(
    val name: String,
    val pingHost: String,
    val sshUser: String = "root",
    val sshPort: Int = 22,
    val panelUrl: String? = null,
)

data class Settings(
    val servers: List<Server> = emptyList(),
    val selected: String? = null,
    val haptics: Boolean = true,
    val showDetails: Boolean = false,
    val notify: Boolean = true,
    val checkUpdates: Boolean = true,
) {
    fun find(name: String?) = servers.firstOrNull { it.name.equals(name, ignoreCase = true) }
    val selectedServer get() = find(selected) ?: servers.firstOrNull()
}

/**
 * Настройки в settings.json и конфиги в configs/ — внутри приватной папки приложения:
 * другие приложения туда не достают, а в резервную копию Android (allowBackup) они не попадают.
 */
class Store(private val dir: File) {
    private val file = File(dir, "settings.json")
    private val configDir = File(dir, "configs").apply { mkdirs() }
    private val _settings = MutableStateFlow(load())
    val settings: StateFlow<Settings> = _settings.asStateFlow()
    val current get() = _settings.value

    fun configFile(name: String) = File(configDir, "$name.conf")

    fun readConfig(name: String): String = configFile(name).readText()

    @Synchronized
    fun update(change: (Settings) -> Settings) {
        val next = change(_settings.value)
        save(next)
        _settings.value = next
    }

    /** Добавить или заменить сервер с тем же именем и сделать его выбранным. */
    fun putServer(server: Server, config: String) {
        writeAtomically(configFile(server.name), config)
        update { s -> s.copy(servers = s.servers.filterNot { it.name.equals(server.name, true) } + server, selected = server.name) }
    }

    fun removeServer(name: String) {
        configFile(name).delete()
        update { s ->
            val rest = s.servers.filterNot { it.name.equals(name, true) }
            s.copy(servers = rest, selected = if (s.selected.equals(name, true)) rest.firstOrNull()?.name else s.selected)
        }
    }

    fun rename(old: String, new: String) {
        if (old == new) return
        val from = configFile(old)
        val to = configFile(new)
        if (!old.equals(new, true) && to.exists()) throw IllegalStateException("Сервер «$new» уже есть")
        val tmp = File(configDir, "$new.renaming")
        from.renameTo(tmp)
        tmp.renameTo(to)
        update { s ->
            s.copy(
                servers = s.servers.map { if (it.name.equals(old, true)) it.copy(name = new) else it },
                selected = if (s.selected.equals(old, true)) new else s.selected,
            )
        }
    }

    fun updateServer(name: String, change: (Server) -> Server) =
        update { s -> s.copy(servers = s.servers.map { if (it.name.equals(name, true)) change(it) else it }) }

    private fun load(): Settings = try {
        val o = JSONObject(file.readText())
        val arr = o.optJSONArray("servers") ?: JSONArray()
        Settings(
            servers = (0 until arr.length()).map { i ->
                val s = arr.getJSONObject(i)
                Server(
                    name = s.getString("name"),
                    pingHost = s.optString("pingHost", "10.8.0.1"),
                    sshUser = s.optString("sshUser", "root"),
                    sshPort = s.optInt("sshPort", 22),
                    panelUrl = s.optString("panelUrl").ifEmpty { null },
                )
            }.filter { configFile(it.name).exists() },
            selected = o.optString("selected").ifEmpty { null },
            haptics = o.optBoolean("haptics", true),
            showDetails = o.optBoolean("showDetails", false),
            notify = o.optBoolean("notify", true),
            checkUpdates = o.optBoolean("checkUpdates", true),
        )
    } catch (_: Exception) {
        Settings()
    }

    private fun save(s: Settings) {
        val o = JSONObject()
            .put("servers", JSONArray(s.servers.map {
                JSONObject().put("name", it.name).put("pingHost", it.pingHost)
                    .put("sshUser", it.sshUser).put("sshPort", it.sshPort).put("panelUrl", it.panelUrl ?: "")
            }))
            .put("selected", s.selected ?: "")
            .put("haptics", s.haptics)
            .put("showDetails", s.showDetails)
            .put("notify", s.notify)
            .put("checkUpdates", s.checkUpdates)
        writeAtomically(file, o.toString(2))
    }

    private fun writeAtomically(target: File, text: String) {
        val tmp = File(target.parentFile, target.name + ".tmp")
        tmp.writeText(text)
        if (!tmp.renameTo(target)) {
            target.delete()
            tmp.renameTo(target)
        }
    }
}
