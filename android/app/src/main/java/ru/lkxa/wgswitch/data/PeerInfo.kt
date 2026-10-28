package ru.lkxa.wgswitch.data

import org.amnezia.awg.config.Config

/**
 * Что о телефоне нужно знать серверу: публичный ключ и адрес в туннеле.
 * Публичный ключ выводится из приватного внутри библиотеки WireGuard; сам приватный наружу не выходит.
 */
data class PeerInfo(val publicKey: String, val address: String?, val presharedKey: String?, val amnezia: Boolean = false) {
    /** Куда на сервере дописывать пира и чем применять: у AmneziaWG свои файл и утилита. */
    val serverFile get() = if (amnezia) "/etc/amnezia/amneziawg/awg0.conf" else "/etc/wireguard/wg0.conf"
    val syncCommand get() = if (amnezia) "sudo bash -c 'awg syncconf awg0 <(awg-quick strip awg0)'" else "sudo bash -c 'wg syncconf wg0 <(wg-quick strip wg0)'"

    /** Готовый блок для конфига сервера. */
    fun serverBlock(name: String): String = buildString {
        append("[Peer]\n# $name · телефон\nPublicKey = $publicKey\n")
        if (presharedKey != null) append("PresharedKey = $presharedKey\n")
        append("AllowedIPs = ${address ?: "10.8.0.X"}/32")
    }

    companion object {
        fun of(config: String): PeerInfo? = try {
            val c = Config.parse(config.byteInputStream())
            val iface = c.`interface`
            val v4 = iface.addresses.firstOrNull { it.address is java.net.Inet4Address }?.address?.hostAddress
            val psk = c.peers.firstOrNull()?.preSharedKey?.orElse(null)?.toBase64()
            PeerInfo(iface.keyPair.publicKey.toBase64(), v4, psk, amnezia = isAmnezia(config))
        } catch (_: Exception) {
            null
        }
    }
}

/** Конфиг AmneziaWG: в [Interface] есть параметры маскировки. */
fun isAmnezia(config: String) = Regex("""(?im)^\s*(Jc|Jmin|S1|H1)\s*=""").containsMatchIn(config)
