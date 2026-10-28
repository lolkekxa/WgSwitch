package ru.lkxa.wgswitch.vpn

import ru.lkxa.wgswitch.data.ConfigText
import java.net.InetAddress
import java.util.concurrent.TimeUnit

/** Пинг сервера внутри туннеля: системный ping (ICMP без root), если его нет — InetAddress.isReachable. */
object Ping {
    private val time = Regex("""time[=<]([\d.]+)""")

    fun ms(host: String, timeoutSec: Int = 2): Int? {
        if (!ConfigText.isValidHost(host)) return null
        return try {
            // аргументы списком, без оболочки — host не может стать командой
            val p = ProcessBuilder("/system/bin/ping", "-c", "1", "-W", timeoutSec.toString(), host).redirectErrorStream(true).start()
            if (!p.waitFor(timeoutSec + 1L, TimeUnit.SECONDS)) {
                p.destroy()
                return null
            }
            val out = p.inputStream.bufferedReader().readText()
            if (p.exitValue() != 0) null else time.find(out)?.groupValues?.get(1)?.toDouble()?.let { maxOf(1, it.toInt()) }
        } catch (_: java.io.IOException) {
            fallback(host, timeoutSec)
        } catch (_: InterruptedException) {
            null
        }
    }

    private fun fallback(host: String, timeoutSec: Int): Int? = try {
        val start = System.nanoTime()
        if (InetAddress.getByName(host).isReachable(timeoutSec * 1000)) maxOf(1, ((System.nanoTime() - start) / 1_000_000).toInt()) else null
    } catch (_: Exception) {
        null
    }
}
