package com.dpitunnel.android

import com.dpitunnel.android.core.AppConfig
import com.dpitunnel.android.core.Socks5Codec
import com.dpitunnel.android.diagnostics.Diagnostics
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress

class CoreLogicTest {

    @Test
    fun hostlistExcludesRootGoogleApisAndGitHub() {
        val raw = """
            # comment
            youtube.com
            googleapis.com
            youtubei.googleapis.com
            github.com
            rutor.info
            RUTOR.INFO
        """.trimIndent()
        val list = AppConfig.filterHostlistLines(raw)
        assertTrue(list.contains("youtube.com"))
        assertTrue(list.contains("youtubei.googleapis.com"))
        assertTrue(list.contains("rutor.info"))
        assertFalse(list.contains("googleapis.com"))
        assertFalse(list.contains("github.com"))
        assertEquals(3, list.size)
    }

    @Test
    fun socks5UdpIpv4RoundTrip() {
        val dst = InetAddress.getByName("77.88.8.8")
        val dnsQuery = byteArrayOf(0x12, 0x34, 0x01, 0x00, 0x00, 0x01)
        val encoded = Socks5Codec.buildUdpPacket(dst, 53, dnsQuery)
        val decoded = Socks5Codec.parseUdpPacket(encoded, encoded.size)
        assertNotNull(decoded)
        assertEquals("77.88.8.8", decoded!!.destHost)
        assertEquals(53, decoded.destPort)
        assertArrayEquals(dnsQuery, decoded.payload)
    }

    @Test
    fun socks5UdpDomainParsing() {
        val domain = "dns.yandex.net".toByteArray(Charsets.US_ASCII)
        val payload = byteArrayOf(0xAA.toByte(), 0xBB.toByte())
        val raw = ByteArray(4 + 1 + domain.size + 2 + payload.size)
        raw[3] = 3
        raw[4] = domain.size.toByte()
        System.arraycopy(domain, 0, raw, 5, domain.size)
        raw[5 + domain.size] = 0x01
        raw[6 + domain.size] = 0xBB.toByte() // 443
        System.arraycopy(payload, 0, raw, 7 + domain.size, payload.size)

        val parsed = Socks5Codec.parseUdpPacket(raw, raw.size)
        assertNotNull(parsed)
        assertEquals("dns.yandex.net", parsed!!.destHost)
        assertEquals(443, parsed.destPort)
        assertArrayEquals(payload, parsed.payload)
    }

    @Test
    fun popularResourcesContainsEightUniqueEntries() {
        val ids = Diagnostics.POPULAR_RESOURCES.map { it.id }
        assertEquals(8, ids.size)
        assertEquals(8, ids.toSet().size)
        assertTrue(ids.containsAll(listOf("youtube", "instagram", "discord", "telegram", "rutracker", "rutor", "x", "medium")))
    }

    @Test
    fun mergeDomainsAddsYoutubeAppDomainsWithoutDuplicates() {
        val existing = "youtube.com\nyoutubei.googleapis.com\n"
        val merged = AppConfig.mergeDomains(existing, AppConfig.YOUTUBE_APP_DOMAINS)
        val lines = AppConfig.filterHostlistLines(merged)
        assertTrue(lines.contains("youtubei.googleapis.com"))
        assertTrue(lines.contains("jnn-pa.googleapis.com"))
        assertFalse(lines.contains("googleapis.com"))
        assertEquals(1 + AppConfig.YOUTUBE_APP_DOMAINS.size, lines.size)
    }

    @Test
    fun tlsClientHelloExpectedLenAndSniExtraction() {
        val host = "youtubei.googleapis.com".toByteArray(Charsets.US_ASCII)
        val serverNameExtLen = 2 + 1 + 2 + host.size
        val extsLen = 4 + serverNameExtLen + 4 + 1400 // simulate large PQ key_share (>1460 bytes total)
        val hsBodyLen = 2 + 32 + 1 + 2 + 2 + 1 + 1 + 2 + extsLen
        val recPayloadLen = 4 + hsBodyLen
        val buf = ByteArray(5 + recPayloadLen)

        // TLS Record Header
        buf[0] = 0x16; buf[1] = 0x03; buf[2] = 0x01
        buf[3] = ((recPayloadLen ushr 8) and 0xFF).toByte()
        buf[4] = (recPayloadLen and 0xFF).toByte()

        // Handshake Header (ClientHello = 0x01)
        buf[5] = 0x01
        buf[6] = ((hsBodyLen ushr 16) and 0xFF).toByte()
        buf[7] = ((hsBodyLen ushr 8) and 0xFF).toByte()
        buf[8] = (hsBodyLen and 0xFF).toByte()

        var p = 9 + 2 + 32
        buf[p++] = 0 // SessionID len
        buf[p++] = 0; buf[p++] = 2; p += 2 // CipherSuites len=2
        buf[p++] = 1; p += 1 // Compression len=1
        buf[p++] = ((extsLen ushr 8) and 0xFF).toByte()
        buf[p++] = (extsLen and 0xFF).toByte()

        // Ext 0: server_name
        buf[p++] = 0; buf[p++] = 0
        buf[p++] = ((serverNameExtLen ushr 8) and 0xFF).toByte()
        buf[p++] = (serverNameExtLen and 0xFF).toByte()
        val listLen = 1 + 2 + host.size
        buf[p++] = ((listLen ushr 8) and 0xFF).toByte()
        buf[p++] = (listLen and 0xFF).toByte()
        buf[p++] = 0 // host_name
        buf[p++] = ((host.size ushr 8) and 0xFF).toByte()
        buf[p++] = (host.size and 0xFF).toByte()
        System.arraycopy(host, 0, buf, p, host.size)

        assertTrue(buf.size > 1460)
        assertEquals(buf.size, Socks5Codec.expectedTlsRecordLength(buf, 1460))
        assertEquals("youtubei.googleapis.com", Socks5Codec.extractTlsSni(buf, buf.size))
    }

    @Test
    fun dnsAaaaSuppressedWhileDnsAForwards() {
        val label = "youtubei".toByteArray(Charsets.US_ASCII)
        fun makeQuery(qtype: Int): ByteArray {
            val q = ByteArray(12 + 1 + label.size + 1 + 4)
            q[0] = 0x12; q[1] = 0x34 // TXID
            q[2] = 0x01; q[3] = 0x00 // RD=1
            q[4] = 0x00; q[5] = 0x01 // QDCOUNT=1
            var p = 12
            q[p++] = label.size.toByte()
            System.arraycopy(label, 0, q, p, label.size); p += label.size
            q[p++] = 0 // root label
            q[p++] = ((qtype ushr 8) and 0xFF).toByte()
            q[p++] = (qtype and 0xFF).toByte()
            q[p++] = 0x00; q[p] = 0x01 // IN
            return q
        }

        val aaaaResp = Socks5Codec.buildEmptyDnsAaaaResponse(makeQuery(28))
        assertNotNull(aaaaResp)
        assertEquals(0x81.toByte(), aaaaResp!![2]) // QR=1, RD=1
        assertEquals(0x80.toByte(), aaaaResp[3])   // RA=1, NOERROR
        assertEquals(0, aaaaResp[7].toInt())       // ANCOUNT=0

        val aResp = Socks5Codec.buildEmptyDnsAaaaResponse(makeQuery(1))
        assertEquals(null, aResp)
    }
}

