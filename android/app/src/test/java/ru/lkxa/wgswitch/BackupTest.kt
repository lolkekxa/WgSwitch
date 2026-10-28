package ru.lkxa.wgswitch

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.lkxa.wgswitch.data.Backup
import ru.lkxa.wgswitch.data.BackupException
import ru.lkxa.wgswitch.data.BackupPayload
import ru.lkxa.wgswitch.data.BackupServer
import ru.lkxa.wgswitch.data.ConfigText
import java.io.File

class BackupTest {
    private val payload = BackupPayload(
        "work",
        listOf(
            BackupServer("home", "10.8.0.1", "[Interface]\nPrivateKey = KEY1=\n"),
            BackupServer("work", "10.9.0.1", "[Interface]\nPrivateKey = KEY2=\n", "admin", 2222, "http://10.9.0.1:8791"),
        ),
    )

    @Test
    fun roundTrip() {
        val back = Backup.read(Backup.write(payload, "correct horse"), "correct horse")
        assertEquals("work", back.selected)
        assertEquals(payload.servers, back.servers)
    }

    @Test
    fun noPlaintext() {
        val raw = String(Backup.write(payload, "correct horse"), Charsets.ISO_8859_1)
        assertFalse(raw.contains("KEY1"))
        assertFalse(raw.contains("10.8.0.1"))
    }

    @Test
    fun wrongPassword() {
        val e = assertThrows(BackupException::class.java) { Backup.read(Backup.write(payload, "correct horse"), "wrong") }
        assertTrue(e.message!!.contains("Неверный пароль"))
    }

    @Test
    fun foreignFile() {
        assertThrows(BackupException::class.java) { Backup.read("hello".toByteArray(), "x") }
    }

    /** PBKDF2-HMAC-SHA256 — вектор из RFC 7914, §11 («passwd», «salt», 1 итерация). */
    @Test
    fun pbkdf2MatchesRfc() {
        val key = Backup.deriveKey("passwd", "salt".toByteArray(), 1)
        val expected = "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc".chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        assertArrayEquals(expected, key)
    }

    /** Копия, сделанная WgSwitch для Windows (BackupService.cs), читается на телефоне. */
    @Test
    fun readsWindowsBackup() {
        val f = File("src/test/resources/from-windows.wgsb")
        if (!f.exists()) return
        val back = Backup.read(f.readBytes(), "correct horse")
        assertEquals(listOf("home", "work"), back.servers.map { it.name })
        assertEquals("work", back.selected)
        assertTrue(back.servers[1].config.contains("PrivateKey = KEY2="))
        assertEquals(2222, back.servers[1].sshPort)
    }

    @Test
    fun configText() {
        assertEquals("10.8.0.1", ConfigText.pingHostFor("[Interface]\nAddress = 10.8.0.2/24\n"))
        assertEquals("10.9.0.1", ConfigText.pingHostFor("[Interface]\nAddress = fd00::2/64, 10.9.0.5/24"))
        assertEquals("10.10.0.1", ConfigText.pingHostFor("[Interface]\naddress=10.10.0.7/32"))
        assertEquals("10.8.0.1", ConfigText.pingHostFor("[Interface]\n"))
        assertEquals("Moj-server-2", ConfigText.nameFrom("Moj server (2).conf"))
        assertEquals("wg0", ConfigText.nameFrom("Мой.conf"))
        assertTrue(ConfigText.isValidName("home"))
        assertFalse(ConfigText.isValidName("слишком-длинное-имя"))
        assertFalse(ConfigText.isValidHost("10.8.0.1; rm"))
        val fromPc = "# WgSwitch name=home ping=10.8.0.1\n[Interface]\nAddress = 10.9.0.3/32\n"
        assertEquals("home" to "10.8.0.1", ConfigText.meta(fromPc))
        assertEquals("10.8.0.1", ConfigText.pingHostFor(fromPc))
        assertEquals(null to null, ConfigText.meta("[Interface]\n"))
        assertEquals(null to "10.8.0.1", ConfigText.meta("# WgSwitch name=слишком-длинное-имя ping=10.8.0.1"))
    }
}
