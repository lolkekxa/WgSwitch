package ru.lkxa.wgswitch

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.lkxa.wgswitch.data.Updates

class UpdatesTest {
    @Test
    fun versions() {
        assertTrue(Updates.isNewer("1.1.2", "1.1.1"))
        assertTrue(Updates.isNewer("1.1.10", "1.1.9"))
        assertTrue(Updates.isNewer("2.0.0", "1.9.9"))
        assertFalse(Updates.isNewer("1.1.1", "1.1.1"))
        assertFalse(Updates.isNewer("1.1.0", "1.1.1"))
        assertFalse(Updates.isNewer("1.2.0-dev.5", "1.2.0"))
        assertTrue(Updates.isNewer("1.2.0", "1.1.1-dev.3"))
    }
}
