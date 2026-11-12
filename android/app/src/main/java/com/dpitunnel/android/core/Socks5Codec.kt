package com.dpitunnel.android.core

import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress

data class Socks5UdpPacket(
    val frag: Int,
    val destHost: String,
    val destPort: Int,
    val payload: ByteArray
)

object Socks5Codec {
    fun parseUdpPacket(buf: ByteArray, length: Int): Socks5UdpPacket? {
        if (length < 10) return null
        if (buf[0] != 0.toByte() || buf[1] != 0.toByte()) return null
        val frag = buf[2].toInt() and 0xFF
        val atyp = buf[3].toInt() and 0xFF
        var pos = 4
        val host: String = when (atyp) {
            1 -> {
                if (length < pos + 4 + 2) return null
                val addr = InetAddress.getByAddress(buf.copyOfRange(pos, pos + 4))
                pos += 4
                addr.hostAddress ?: return null
            }
            3 -> {
                if (length < pos + 1) return null
                val dLen = buf[pos].toInt() and 0xFF
                pos += 1
                if (length < pos + dLen + 2) return null
                val name = String(buf, pos, dLen, Charsets.US_ASCII)
                pos += dLen
                name
            }
            4 -> {
                if (length < pos + 16 + 2) return null
                val addr = InetAddress.getByAddress(buf.copyOfRange(pos, pos + 16))
                pos += 16
                addr.hostAddress ?: return null
            }
            else -> return null
        }
        val port = ((buf[pos].toInt() and 0xFF) shl 8) or (buf[pos + 1].toInt() and 0xFF)
        pos += 2
        val payload = buf.copyOfRange(pos, length)
        return Socks5UdpPacket(frag, host, port, payload)
    }

    fun buildUdpPacket(srcAddr: InetAddress, srcPort: Int, payload: ByteArray): ByteArray {
        val rawAddr = srcAddr.address
        val atyp: Byte = when (srcAddr) {
            is Inet4Address -> 1
            is Inet6Address -> 4
            else -> 1
        }
        val out = ByteArray(4 + rawAddr.size + 2 + payload.size)
        out[0] = 0
        out[1] = 0
        out[2] = 0
        out[3] = atyp
        System.arraycopy(rawAddr, 0, out, 4, rawAddr.size)
        val pOff = 4 + rawAddr.size
        out[pOff] = ((srcPort ushr 8) and 0xFF).toByte()
        out[pOff + 1] = (srcPort and 0xFF).toByte()
        System.arraycopy(payload, 0, out, pOff + 2, payload.size)
        return out
    }

    /**
     * Returns total expected TLS record length (5 + payload length) if [buf] starts with
     * a TLS Handshake record (0x16 0x03 ...), or -1 otherwise.
     */
    fun expectedTlsRecordLength(buf: ByteArray, len: Int): Int {
        if (len < 5) return -1
        if ((buf[0].toInt() and 0xFF) != 0x16) return -1
        if ((buf[1].toInt() and 0xFF) != 0x03) return -1
        val recLen = ((buf[3].toInt() and 0xFF) shl 8) or (buf[4].toInt() and 0xFF)
        if (recLen <= 0) return -1
        return 5 + recLen
    }

    /**
     * Extracts SNI server_name from a TLS ClientHello record buffer, or returns null.
     */
    fun extractTlsSni(buf: ByteArray, len: Int): String? {
        if (len < 44) return null
        if ((buf[0].toInt() and 0xFF) != 0x16 || (buf[5].toInt() and 0xFF) != 0x01) return null
        var pos = 5 + 4 + 2 + 32 // record(5) + handshakeHdr(4) + clientVer(2) + random(32)
        if (pos >= len) return null
        val sidLen = buf[pos].toInt() and 0xFF
        pos += 1 + sidLen
        if (pos + 2 > len) return null
        val csLen = ((buf[pos].toInt() and 0xFF) shl 8) or (buf[pos + 1].toInt() and 0xFF)
        pos += 2 + csLen
        if (pos + 1 > len) return null
        val compLen = buf[pos].toInt() and 0xFF
        pos += 1 + compLen
        if (pos + 2 > len) return null
        val extTotalLen = ((buf[pos].toInt() and 0xFF) shl 8) or (buf[pos + 1].toInt() and 0xFF)
        pos += 2
        val extEnd = minOf(len, pos + extTotalLen)
        while (pos + 4 <= extEnd) {
            val extType = ((buf[pos].toInt() and 0xFF) shl 8) or (buf[pos + 1].toInt() and 0xFF)
            val extLen = ((buf[pos + 2].toInt() and 0xFF) shl 8) or (buf[pos + 3].toInt() and 0xFF)
            pos += 4
            if (pos + extLen > extEnd) break
            if (extType == 0 && extLen >= 5) {
                val listLen = ((buf[pos].toInt() and 0xFF) shl 8) or (buf[pos + 1].toInt() and 0xFF)
                var p = pos + 2
                val listEnd = minOf(pos + extLen, p + listLen)
                while (p + 3 <= listEnd) {
                    val nameType = buf[p].toInt() and 0xFF
                    val nameLen = ((buf[p + 1].toInt() and 0xFF) shl 8) or (buf[p + 2].toInt() and 0xFF)
                    p += 3
                    if (p + nameLen > listEnd) break
                    if (nameType == 0 && nameLen > 0) {
                        return String(buf, p, nameLen, Charsets.US_ASCII)
                    }
                    p += nameLen
                }
            }
            pos += extLen
        }
        return null
    }

    /**
     * If [dnsQuery] is a standard single-question AAAA (QTYPE=28) DNS query, builds a valid
     * NOERROR empty response (ANCOUNT=0) so dual-stack Android clients use IPv4 cleanly.
     */
    fun buildEmptyDnsAaaaResponse(dnsQuery: ByteArray): ByteArray? {
        if (dnsQuery.size < 17) return null
        val qr = (dnsQuery[2].toInt() and 0x80) != 0
        if (qr) return null
        val qdCount = ((dnsQuery[4].toInt() and 0xFF) shl 8) or (dnsQuery[5].toInt() and 0xFF)
        if (qdCount != 1) return null

        var pos = 12
        while (pos < dnsQuery.size) {
            val labelLen = dnsQuery[pos].toInt() and 0xFF
            if (labelLen == 0) {
                pos += 1
                break
            }
            if ((labelLen and 0xC0) != 0) return null
            pos += 1 + labelLen
        }
        if (pos + 4 > dnsQuery.size) return null
        val qtype = ((dnsQuery[pos].toInt() and 0xFF) shl 8) or (dnsQuery[pos + 1].toInt() and 0xFF)
        val qclass = ((dnsQuery[pos + 2].toInt() and 0xFF) shl 8) or (dnsQuery[pos + 3].toInt() and 0xFF)
        if (qtype != 28 || qclass != 1) return null

        val respLen = pos + 4
        val resp = ByteArray(respLen)
        System.arraycopy(dnsQuery, 0, resp, 0, respLen)
        val rd = dnsQuery[2].toInt() and 0x01
        resp[2] = (0x80 or rd).toByte() // QR=1, Opcode=0, RD preserved
        resp[3] = 0x80.toByte()        // RA=1, RCODE=0 (NOERROR)
        resp[6] = 0; resp[7] = 0        // ANCOUNT=0
        resp[8] = 0; resp[9] = 0        // NSCOUNT=0
        resp[10] = 0; resp[11] = 0      // ARCOUNT=0
        return resp
    }
}
