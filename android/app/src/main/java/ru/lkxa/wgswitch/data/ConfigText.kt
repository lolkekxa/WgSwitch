package ru.lkxa.wgswitch.data

/** Разбор текста конфига без библиотеки WireGuard: имя туннеля и адрес сервера внутри него. */
object ConfigText {
    private val validName = Regex("^[a-zA-Z0-9_=+.-]{1,15}$")
    private val ipv4 = Regex("""^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:/(\d{1,2}))?$""")
    private val hostChars = Regex("^[a-zA-Z0-9.:-]{1,253}$")

    /** Как у WireGuard для Windows: латиница, цифры и _=+.- , до 15 символов. */
    fun isValidName(name: String) = validName.matches(name)

    fun isValidHost(host: String) = hostChars.matches(host)

    /** «Мой сервер (2).conf» → «Moj-server-2»: лишнее заменяется, длина — до 15. */
    fun nameFrom(fileName: String?): String {
        val base = (fileName ?: "").substringAfterLast('/').removeSuffix(".conf").removeSuffix(".CONF")
        val clean = base.map { if (it.isLetterOrDigit() && it.code < 128 || it in "_=+.-") it else '-' }
            .joinToString("").trim('-').replace(Regex("-{2,}"), "-").take(15).trim('-')
        return clean.ifEmpty { "wg0" }
    }

    /**
     * Адрес сервера в туннеле — первый адрес подсети клиента: Address = 10.8.0.2/24 → 10.8.0.1.
     * Так устроены почти все конфиги; если нет — пользователь поправит в настройках.
     */
    fun pingHostFor(config: String): String {
        meta(config).second?.let { return it }
        val line = Regex("""(?im)^\s*Address\s*=\s*([^\r\n#]+)""").find(config)?.groupValues?.get(1) ?: return DEFAULT_HOST
        val m = line.split(',').map { it.trim() }.firstNotNullOfOrNull { ipv4.matchEntire(it) } ?: return DEFAULT_HOST
        val octets = (1..4).map { m.groupValues[it].toInt() }
        if (octets.any { it > 255 }) return DEFAULT_HOST
        val prefix = m.groupValues[5].toIntOrNull()?.takeIf { it in 8..30 } ?: 24
        val ip = octets.fold(0L) { acc, o -> acc * 256 + o }
        val mask = (0xFFFFFFFFL shl (32 - prefix)) and 0xFFFFFFFFL
        val host = (ip and mask) + 1
        return listOf(24, 16, 8, 0).joinToString(".") { ((host shr it) and 255).toString() }
    }

    /**
     * Подсказка от WgSwitch на ПК в первой строке QR: «# WgSwitch name=home ping=10.8.0.1».
     * Для WireGuard это комментарий, для нас — имя и IP для проверки без ручного ввода.
     */
    fun meta(config: String): Pair<String?, String?> {
        val m = Regex("""(?m)^\s*#\s*WgSwitch\b([^\r\n]*)""").find(config) ?: return null to null
        val name = Regex("""\bname=(\S+)""").find(m.groupValues[1])?.groupValues?.get(1)?.takeIf(::isValidName)
        val ping = Regex("""\bping=(\S+)""").find(m.groupValues[1])?.groupValues?.get(1)?.takeIf(::isValidHost)
        return name to ping
    }

    const val DEFAULT_HOST = "10.8.0.1"
}
