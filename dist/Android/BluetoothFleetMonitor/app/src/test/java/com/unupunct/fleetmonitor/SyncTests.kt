package com.unupunct.fleetmonitor

import com.unupunct.fleetmonitor.sync.PairingLink
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class SyncTests {
    private val fp = "AB".repeat(32)

    @Test fun parses_the_link_the_windows_app_builds() {
        // same format as PairingLink.Build in the Windows app (pinned by a test there)
        val l = PairingLink.parse("bfm://pair?h=192.168.1.20&p=47821&f=$fp&c=12345678&n=STUDIO%20PC")
        assertNotNull(l)
        assertEquals("192.168.1.20", l!!.host); assertEquals(47821, l.port); assertEquals(fp, l.fingerprint); assertEquals("12345678", l.code); assertEquals("STUDIO PC", l.serverName)
    }

    @Test fun rejects_anything_that_is_not_a_complete_pairing_link() {
        assertNull(PairingLink.parse(null))
        assertNull(PairingLink.parse("https://example.com/pair?h=1.2.3.4&p=1&f=$fp&c=12345678"))
        assertNull(PairingLink.parse("bfm://pair?h=192.168.1.20&p=47821&f=${fp.dropLast(2)}&c=12345678"))      // short fingerprint
        assertNull(PairingLink.parse("bfm://pair?h=192.168.1.20&p=47821&f=$fp&c=1234"))                       // short code
        assertNull(PairingLink.parse("bfm://pair?h=&p=47821&f=$fp&c=12345678"))                               // no host
        assertNull(PairingLink.parse("bfm://pair?h=192.168.1.20&p=70000&f=$fp&c=12345678"))                   // port out of range
        assertNull(PairingLink.parse("bfm://pair?h=192.168.1.20&p=abc&f=$fp&c=12345678"))
    }

    @Test fun fingerprint_is_upper_cased_and_grouped_for_display() {
        val l = PairingLink.parse("bfm://pair?h=h&p=1&f=${fp.lowercase()}&c=12345678")!!
        assertEquals(fp, l.fingerprint)
        assertEquals("ABAB ABAB", l.fingerprintDisplay.take(9))
    }
}
