package ru.lkxa.wgswitch.data

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings as AndroidSettings
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** Релиз на GitHub, в котором есть APK. */
data class Release(val version: String, val apkUrl: String, val apkName: String, val apkSize: Long, val sumsUrl: String?, val page: String)

/** Что сейчас с обновлением: показывается строкой под карточкой на главной. */
sealed interface UpdateState {
    data object None : UpdateState
    data class Available(val release: Release) : UpdateState
    data class Downloading(val release: Release, val progress: Float) : UpdateState
    data class Failed(val release: Release, val message: String) : UpdateState
}

/**
 * Обновление с GitHub, как у Windows-версии: последний релиз → APK → проверка SHA-256 по SHA256SUMS.txt →
 * системная установка. Ставится поверх, потому что все APK подписаны одним постоянным ключом.
 */
class Updates(private val context: Context, private val store: Store) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val _state = MutableStateFlow<UpdateState>(UpdateState.None)
    val state: StateFlow<UpdateState> = _state.asStateFlow()
    private var lastCheck = 0L

    /** При запуске и не чаще раза в 12 часов — если проверка включена. */
    fun checkIfDue() {
        if (!store.current.checkUpdates || System.currentTimeMillis() - lastCheck < 12 * 3600_000L) return
        scope.launch { check() }
    }

    /** Ручная проверка из настроек: true — вышла новая версия, false — нет, null — не удалось. */
    suspend fun check(): Boolean? {
        lastCheck = System.currentTimeMillis()
        val r = try { withContext(Dispatchers.IO) { latest() } } catch (_: Exception) { return null } ?: return false
        val newer = isNewer(r.version, currentVersion())
        if (newer && _state.value !is UpdateState.Downloading) _state.value = UpdateState.Available(r)
        if (!newer) _state.value = UpdateState.None
        return newer
    }

    fun currentVersion(): String = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0"

    /** Скачать, проверить и открыть установку. Без разрешения «Установка из этого источника» — сначала в настройки Android. */
    fun install() {
        val r = when (val s = _state.value) {
            is UpdateState.Available -> s.release
            is UpdateState.Failed -> s.release
            else -> return
        }
        if (!context.packageManager.canRequestPackageInstalls()) {
            context.startActivity(
                Intent(AndroidSettings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
            return
        }
        scope.launch {
            _state.value = UpdateState.Downloading(r, 0f)
            try {
                val file = withContext(Dispatchers.IO) { download(r) { p -> _state.value = UpdateState.Downloading(r, p) } }
                _state.value = UpdateState.Available(r)
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                context.startActivity(
                    Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK),
                )
            } catch (e: Exception) {
                _state.value = UpdateState.Failed(r, e.message ?: "не удалось скачать")
            }
        }
    }

    private fun latest(): Release? {
        val json = JSONObject(get(API).readText())
        val tag = json.getString("tag_name")
        val assets = json.getJSONArray("assets")
        var apk: JSONObject? = null
        var sums: String? = null
        for (i in 0 until assets.length()) {
            val a = assets.getJSONObject(i)
            val name = a.getString("name")
            if (name.endsWith(".apk", true)) apk = a
            if (name.equals("SHA256SUMS.txt", true)) sums = a.getString("browser_download_url")
        }
        apk ?: return null // в релизе нет APK — обновляться не на что
        return Release(
            version = tag.removePrefix("v"),
            apkUrl = apk.getString("browser_download_url"),
            apkName = apk.getString("name"),
            apkSize = apk.optLong("size"),
            sumsUrl = sums,
            page = json.optString("html_url", "https://github.com/lolkekxa/WgSwitch/releases/latest"),
        )
    }

    private fun download(r: Release, progress: (Float) -> Unit): File {
        val dir = File(context.cacheDir, "updates").apply { deleteRecursively(); mkdirs() }
        val file = File(dir, r.apkName.replace(Regex("[^A-Za-z0-9._-]"), "_"))
        val sha = MessageDigest.getInstance("SHA-256")
        val conn = open(r.apkUrl)
        val total = conn.contentLengthLong.takeIf { it > 0 } ?: r.apkSize
        conn.inputStream.use { input ->
            file.outputStream().use { out ->
                val buf = ByteArray(64 * 1024)
                var done = 0L
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n)
                    sha.update(buf, 0, n)
                    done += n
                    if (total > 0) progress((done.toFloat() / total).coerceIn(0f, 1f))
                }
            }
        }
        val actual = sha.digest().joinToString("") { "%02x".format(it) }
        val expected = r.sumsUrl?.let { url ->
            get(url).readText().lines().map { it.trim().split(Regex("\\s+")) }
                .firstOrNull { it.size >= 2 && it[1].trimStart('*').equals(r.apkName, true) }?.get(0)?.lowercase()
        }
        if (expected == null || expected != actual) {
            file.delete()
            throw IllegalStateException(if (expected == null) "в релизе нет контрольной суммы APK" else "контрольная сумма не совпала — файл не установлен")
        }
        return file
    }

    private fun open(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 30_000
            instanceFollowRedirects = true
            setRequestProperty("User-Agent", "WgSwitch-Android")
            setRequestProperty("Accept", "application/vnd.github+json")
            if (responseCode !in 200..299) throw IllegalStateException("GitHub ответил $responseCode")
        }

    private fun get(url: String) = open(url).inputStream.bufferedReader()

    companion object {
        private const val API = "https://api.github.com/repos/lolkekxa/WgSwitch/releases/latest"

        /** Сравнение по числам до «-»: 1.1.10 > 1.1.9, пре-релизы (1.2.0-dev.5) не считаются новее 1.2.0. */
        fun isNewer(remote: String, local: String): Boolean {
            fun parts(v: String) = v.substringBefore('-').split('.').map { it.toIntOrNull() ?: 0 }
            val a = parts(remote)
            val b = parts(local)
            for (i in 0 until maxOf(a.size, b.size)) {
                val x = a.getOrElse(i) { 0 }
                val y = b.getOrElse(i) { 0 }
                if (x != y) return x > y
            }
            return false
        }
    }
}
