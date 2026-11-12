package com.dpitunnel.android.proxy

import com.dpitunnel.android.core.RuntimeLogger
import com.dpitunnel.android.core.Socks5Codec
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketAddress
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class LocalSocksBridge(
    private val listenPort: Int,
    @Volatile var tpwsPort: Int,
    @Volatile var dnsHost: String,
    @Volatile var dnsPort: Int,
    @Volatile var blockQuic: Boolean
) {
    @Volatile
    private var running = false
    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null
    private var pool: ExecutorService? = null
    private val loggedHosts = java.util.Collections.newSetFromMap(java.util.concurrent.ConcurrentHashMap<String, Boolean>())

    fun start(): Boolean {
        if (running) return true
        return try {
            val loopback = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
            val ss = ServerSocket()
            ss.reuseAddress = true
            ss.bind(InetSocketAddress(loopback, listenPort), 128)
            serverSocket = ss
            pool = Executors.newCachedThreadPool()
            running = true

            acceptThread = Thread({
                RuntimeLogger.write("BRIDGE", "SOCKS5+DNS bridge listening on 127.0.0.1:$listenPort -> tpws:$tpwsPort, dns=$dnsHost:$dnsPort, blockQuic=$blockQuic")
                while (running) {
                    try {
                        val client = ss.accept()
                        pool?.execute { handleClient(client) }
                    } catch (_: Exception) {
                        if (!running) break
                    }
                }
            }, "LocalSocksBridge-Accept").apply { isDaemon = true; start() }
            true
        } catch (e: Exception) {
            RuntimeLogger.write("BRIDGE-ERR", "Start failed: ${e.message}")
            stop()
            false
        }
    }

    fun stop() {
        running = false
        try { serverSocket?.close() } catch (_: Exception) {}
        serverSocket = null
        try { pool?.shutdownNow() } catch (_: Exception) {}
        pool = null
    }

    private fun handleClient(client: Socket) {
        var upstream: Socket? = null
        try {
            client.tcpNoDelay = true
            client.soTimeout = 15000
            val cin = client.getInputStream()
            val cout = client.getOutputStream()

            val ver = cin.read()
            val nMethods = cin.read()
            if (ver != 5 || nMethods <= 0) {
                client.close()
                return
            }
            val methods = ByteArray(nMethods)
            if (!readFully(cin, methods)) {
                client.close()
                return
            }
            cout.write(byteArrayOf(0x05, 0x00))
            cout.flush()

            val reqHead = ByteArray(4)
            if (!readFully(cin, reqHead) || reqHead[0].toInt() != 5) {
                client.close()
                return
            }
            val cmd = reqHead[1].toInt() and 0xFF
            val atyp = reqHead[3].toInt() and 0xFF

            val addrBytesOut = ByteArrayOutputStream()
            addrBytesOut.write(reqHead)
            val destHost: String = when (atyp) {
                1 -> {
                    val b = ByteArray(4)
                    if (!readFully(cin, b)) return
                    addrBytesOut.write(b)
                    InetAddress.getByAddress(b).hostAddress ?: return
                }
                3 -> {
                    val len = cin.read()
                    if (len <= 0) return
                    val b = ByteArray(len)
                    if (!readFully(cin, b)) return
                    addrBytesOut.write(len)
                    addrBytesOut.write(b)
                    String(b, Charsets.US_ASCII)
                }
                4 -> {
                    val b = ByteArray(16)
                    if (!readFully(cin, b)) return
                    addrBytesOut.write(b)
                    InetAddress.getByAddress(b).hostAddress ?: return
                }
                else -> {
                    sendReply(cout, 0x08, 0)
                    return
                }
            }
            val portBytes = ByteArray(2)
            if (!readFully(cin, portBytes)) return
            addrBytesOut.write(portBytes)
            val destPort = ((portBytes[0].toInt() and 0xFF) shl 8) or (portBytes[1].toInt() and 0xFF)

            if (cmd == 0x03) {
                handleUdpAssociate(client, cin, cout)
                return
            }
            if (cmd != 0x01) {
                sendReply(cout, 0x07, 0)
                return
            }

            client.soTimeout = 0
            // Reject IPv6 TCP immediately so Happy Eyeballs / Cronet (YouTube app) falls back to IPv4 -> tpws in <1ms
            if (atyp == 4) {
                sendReply(cout, 0x04, 0)
                return
            }
            val activeTpws = tpwsPort
            if ((destPort == 80 || destPort == 443) && activeTpws > 0) {
                upstream = connectViaTpws(activeTpws, addrBytesOut.toByteArray(), cout)
            }
            if (upstream == null) {
                upstream = Socket()
                upstream.tcpNoDelay = true
                upstream.connect(InetSocketAddress(destHost, destPort), 6000)
                sendReply(cout, 0x00, upstream.localPort)
            }

            if (destPort == 443) {
                forwardInitialTlsClientHello(client, upstream, destHost)
            }
            relayBidirectional(client, upstream)
        } catch (_: Exception) {
        } finally {
            try { upstream?.close() } catch (_: Exception) {}
            try { client.close() } catch (_: Exception) {}
        }
    }

    private fun forwardInitialTlsClientHello(client: Socket, upstream: Socket, destHost: String) {
        val cin = client.getInputStream()
        val uout = upstream.getOutputStream()
        val firstBuf = ByteArray(16384)
        val n0 = cin.read(firstBuf)
        if (n0 <= 0) return

        val expectedLen = Socks5Codec.expectedTlsRecordLength(firstBuf, n0)
        if (expectedLen in (n0 + 1)..16384) {
            val fullBuf = ByteArray(expectedLen)
            System.arraycopy(firstBuf, 0, fullBuf, 0, n0)
            var total = n0
            val prevTimeout = client.soTimeout
            try {
                client.soTimeout = 300
                while (total < expectedLen) {
                    val more = try {
                        cin.read(fullBuf, total, expectedLen - total)
                    } catch (_: Exception) {
                        -1
                    }
                    if (more <= 0) break
                    total += more
                }
            } finally {
                try { client.soTimeout = prevTimeout } catch (_: Exception) {}
            }
            val sni = Socks5Codec.extractTlsSni(fullBuf, total)
            if (sni != null && loggedHosts.add(sni)) {
                RuntimeLogger.write("TLS", "$sni (${total}B coalesced, $destHost)")
            }
            uout.write(fullBuf, 0, total)
            uout.flush()
            return
        }

        val sni = Socks5Codec.extractTlsSni(firstBuf, n0)
        if (sni != null && loggedHosts.add(sni)) {
            RuntimeLogger.write("TLS", "$sni (${n0}B, $destHost)")
        }
        uout.write(firstBuf, 0, n0)
        uout.flush()
    }

    private fun connectViaTpws(port: Int, rawSocksReq: ByteArray, clientOut: OutputStream): Socket? {
        var s: Socket? = null
        return try {
            s = Socket()
            s.tcpNoDelay = true
            s.connect(InetSocketAddress("127.0.0.1", port), 4000)
            s.soTimeout = 10000
            val sin = s.getInputStream()
            val sout = s.getOutputStream()

            sout.write(byteArrayOf(0x05, 0x01, 0x00))
            sout.flush()
            val greet = ByteArray(2)
            if (!readFully(sin, greet) || greet[1].toInt() != 0) {
                s.close()
                return null
            }

            sout.write(rawSocksReq)
            sout.flush()

            val repHead = ByteArray(4)
            if (!readFully(sin, repHead)) {
                s.close()
                return null
            }
            val bndAtyp = repHead[3].toInt() and 0xFF
            val tailLen = when (bndAtyp) {
                1 -> 4 + 2
                4 -> 16 + 2
                3 -> {
                    val dl = sin.read()
                    if (dl < 0) { s.close(); return null }
                    val b = ByteArray(dl + 2)
                    if (!readFully(sin, b)) { s.close(); return null }
                    clientOut.write(repHead)
                    clientOut.write(dl)
                    clientOut.write(b)
                    clientOut.flush()
                    s.soTimeout = 0
                    return s
                }
                else -> { s.close(); return null }
            }
            val tail = ByteArray(tailLen)
            if (!readFully(sin, tail)) {
                s.close()
                return null
            }
            clientOut.write(repHead)
            clientOut.write(tail)
            clientOut.flush()
            s.soTimeout = 0
            s
        } catch (_: Exception) {
            try { s?.close() } catch (_: Exception) {}
            null
        }
    }

    private fun handleUdpAssociate(ctrlSock: Socket, cin: InputStream, cout: OutputStream) {
        val loopback = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
        val udpSock = DatagramSocket(0, loopback)
        udpSock.soTimeout = 5000
        sendReply(cout, 0x00, udpSock.localPort)

        val relayThread = Thread({
            val buf = ByteArray(4096)
            var lastPeer: SocketAddress? = null
            while (running && !ctrlSock.isClosed && !udpSock.isClosed) {
                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    udpSock.receive(pkt)
                    val peer = pkt.socketAddress
                    lastPeer = peer
                    val parsed = Socks5Codec.parseUdpPacket(pkt.data, pkt.length) ?: continue
                    if (parsed.destPort == 443 && blockQuic) {
                        continue // Drop QUIC UDP:443 to force TLS/TCP fallback
                    }
                    pool?.execute {
                        forwardUdp(udpSock, peer, parsed.destHost, parsed.destPort, parsed.payload)
                    }
                } catch (_: Exception) {
                    if (ctrlSock.isClosed || !running) break
                }
            }
            try { udpSock.close() } catch (_: Exception) {}
        }, "Socks5UdpRelay").apply { isDaemon = true; start() }

        try {
            ctrlSock.soTimeout = 0
            val tmp = ByteArray(64)
            while (running && cin.read(tmp) >= 0) {
                // Keep UDP association alive until TCP control closes
            }
        } catch (_: Exception) {
        } finally {
            try { udpSock.close() } catch (_: Exception) {}
            try { relayThread.interrupt() } catch (_: Exception) {}
        }
    }

    private fun forwardUdp(
        udpSock: DatagramSocket,
        clientPeer: SocketAddress,
        origDestHost: String,
        origDestPort: Int,
        payload: ByteArray
    ) {
        try {
            if (origDestPort == 53) {
                val emptyAaaa = Socks5Codec.buildEmptyDnsAaaaResponse(payload)
                if (emptyAaaa != null && !udpSock.isClosed) {
                    val replyFromAddr = InetAddress.getByName(origDestHost)
                    val wrapped = Socks5Codec.buildUdpPacket(replyFromAddr, origDestPort, emptyAaaa)
                    udpSock.send(DatagramPacket(wrapped, wrapped.size, clientPeer))
                    return
                }
            }
            val targetHost = if (origDestPort == 53) dnsHost else origDestHost
            val targetPort = if (origDestPort == 53) dnsPort else origDestPort
            val targetAddr = InetAddress.getByName(targetHost)

            val respPayload = querySingleUdp(targetAddr, targetPort, payload, 1800)
                ?: if (origDestPort == 53) {
                    querySingleUdp(InetAddress.getByName("8.8.8.8"), 53, payload, 1800)
                } else null

            if (respPayload != null && !udpSock.isClosed) {
                val replyFromAddr = InetAddress.getByName(origDestHost)
                val wrapped = Socks5Codec.buildUdpPacket(replyFromAddr, origDestPort, respPayload)
                udpSock.send(DatagramPacket(wrapped, wrapped.size, clientPeer))
            }
        } catch (_: Exception) {
        }
    }

    private fun querySingleUdp(addr: InetAddress, port: Int, payload: ByteArray, timeoutMs: Int): ByteArray? {
        var ds: DatagramSocket? = null
        return try {
            ds = DatagramSocket()
            ds.soTimeout = timeoutMs
            ds.send(DatagramPacket(payload, payload.size, addr, port))
            val rbuf = ByteArray(4096)
            val rp = DatagramPacket(rbuf, rbuf.size)
            ds.receive(rp)
            rp.data.copyOfRange(0, rp.length)
        } catch (_: Exception) {
            null
        } finally {
            try { ds?.close() } catch (_: Exception) {}
        }
    }

    private fun relayBidirectional(a: Socket, b: Socket) {
        val t1 = pool?.submit { copyStream(a.getInputStream(), b.getOutputStream(), a, b) }
        copyStream(b.getInputStream(), a.getOutputStream(), b, a)
        try { t1?.get() } catch (_: Exception) {}
    }

    private fun copyStream(inp: InputStream, out: OutputStream, sIn: Socket, sOut: Socket) {
        val buf = ByteArray(16384)
        try {
            while (true) {
                val n = inp.read(buf)
                if (n <= 0) break
                out.write(buf, 0, n)
                out.flush()
            }
        } catch (_: Exception) {
        } finally {
            try { sOut.shutdownOutput() } catch (_: Exception) {}
        }
    }

    private fun sendReply(out: OutputStream, rep: Int, port: Int) {
        out.write(
            byteArrayOf(
                0x05, rep.toByte(), 0x00, 0x01,
                127, 0, 0, 1,
                ((port ushr 8) and 0xFF).toByte(),
                (port and 0xFF).toByte()
            )
        )
        out.flush()
    }

    private fun readFully(inp: InputStream, buf: ByteArray): Boolean {
        var off = 0
        while (off < buf.size) {
            val n = inp.read(buf, off, buf.size - off)
            if (n <= 0) return false
            off += n
        }
        return true
    }
}

