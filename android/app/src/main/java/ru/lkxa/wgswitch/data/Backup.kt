package ru.lkxa.wgswitch.data

import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

data class BackupServer(
    val name: String,
    val pingHost: String,
    val config: String,
    val sshUser: String = "root",
    val sshPort: Int = 22,
    val panelUrl: String? = null,
)

data class BackupPayload(val selected: String?, val servers: List<BackupServer>)

class BackupException(message: String) : Exception(message)

/**
 * Тот же формат .wgsb, что у WgSwitch для Windows, — копия переносится между ПК и телефоном в обе стороны.
 * "WGSB1" | соль 16 | nonce 12 | tag 16 | AES-256-GCM(JSON); ключ — PBKDF2-HMAC-SHA256, 600 000 итераций.
 * Поля JSON названы как в .NET (PascalCase).
 */
object Backup {
    const val EXTENSION = ".wgsb"
    const val MIN_PASSWORD = 8

    private val magic = "WGSB1".toByteArray()
    private const val ITERATIONS = 600_000
    private const val SALT = 16
    private const val NONCE = 12
    private const val TAG = 16

    fun defaultFileName(): String = "WgSwitch-" + LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd")) + EXTENSION

    fun read(data: ByteArray, password: String): BackupPayload {
        val header = magic.size + SALT + NONCE + TAG
        if (data.size <= header || !data.copyOfRange(0, magic.size).contentEquals(magic))
            throw BackupException("Это не резервная копия WgSwitch")

        val salt = data.copyOfRange(magic.size, magic.size + SALT)
        val nonce = data.copyOfRange(magic.size + SALT, magic.size + SALT + NONCE)
        val tag = data.copyOfRange(magic.size + SALT + NONCE, header)
        val cipherText = data.copyOfRange(header, data.size)

        val key = deriveKey(password, salt)
        val plain = try {
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG * 8, nonce))
            c.doFinal(cipherText + tag) // в Java тег идёт в конце шифротекста
        } catch (_: AEADBadTagException) {
            throw BackupException("Неверный пароль или файл повреждён")
        } finally {
            key.fill(0)
        }

        try {
            val o = JSONObject(String(plain, Charsets.UTF_8))
            val arr = o.optJSONArray("Servers") ?: JSONArray()
            return BackupPayload(
                selected = o.optString("Selected").takeIf { it.isNotEmpty() && it != "null" },
                servers = (0 until arr.length()).map { i ->
                    val s = arr.getJSONObject(i)
                    BackupServer(
                        name = s.getString("Name"),
                        pingHost = s.optString("PingHost", ConfigText.DEFAULT_HOST),
                        config = s.getString("Config"),
                        sshUser = s.optString("SshUser", "root"),
                        sshPort = s.optInt("SshPort", 22),
                        panelUrl = if (s.isNull("PanelUrl")) null else s.optString("PanelUrl").ifEmpty { null },
                    )
                },
            )
        } catch (e: org.json.JSONException) {
            throw BackupException("Копия повреждена")
        } finally {
            plain.fill(0)
        }
    }

    fun write(payload: BackupPayload, password: String): ByteArray {
        val json = JSONObject()
            .put("Version", 1)
            .put("Created", LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss")))
            .put("Selected", payload.selected ?: JSONObject.NULL)
            .put("Servers", JSONArray(payload.servers.map {
                JSONObject()
                    .put("Name", it.name).put("PingHost", it.pingHost).put("Config", it.config)
                    .put("AutoConnect", false).put("SshUser", it.sshUser).put("SshPort", it.sshPort)
                    .put("PanelUrl", it.panelUrl ?: JSONObject.NULL)
            }))
        val plain = json.toString().toByteArray(Charsets.UTF_8)
        val rnd = SecureRandom()
        val salt = ByteArray(SALT).also(rnd::nextBytes)
        val nonce = ByteArray(NONCE).also(rnd::nextBytes)
        val key = deriveKey(password, salt)
        try {
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG * 8, nonce))
            val out = c.doFinal(plain)
            val cipherText = out.copyOfRange(0, out.size - TAG)
            val tag = out.copyOfRange(out.size - TAG, out.size)
            return magic + salt + nonce + tag + cipherText
        } finally {
            key.fill(0)
            plain.fill(0)
        }
    }

    /**
     * PBKDF2-HMAC-SHA256 вручную: у SecretKeyFactory на разных Android по-разному кодируется пароль,
     * а здесь он ровно как в .NET — UTF-8. Один блок, потому что ключ 32 байта.
     */
    internal fun deriveKey(password: String, salt: ByteArray, iterations: Int = ITERATIONS): ByteArray {
        val pass = password.toByteArray(Charsets.UTF_8)
        if (pass.isEmpty()) throw BackupException("Пустой пароль")
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pass, "HmacSHA256"))
        pass.fill(0)
        mac.update(salt)
        var u = mac.doFinal(byteArrayOf(0, 0, 0, 1))
        val t = u.copyOf()
        repeat(iterations - 1) {
            u = mac.doFinal(u)
            for (i in t.indices) t[i] = (t[i].toInt() xor u[i].toInt()).toByte()
        }
        return t
    }
}
