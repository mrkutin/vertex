package ru.vertices.android.core.probe

import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ServerSocket

class TcpRttTest {

    private val servers = mutableListOf<ServerSocket>()

    @After fun tearDown() {
        servers.forEach { runCatching { it.close() } }
        servers.clear()
    }

    private fun startEchoServer(): ServerSocket {
        val s = ServerSocket(0)
        servers.add(s)
        Thread {
            while (!s.isClosed) {
                runCatching { s.accept().close() }
            }
        }.apply { isDaemon = true }.start()
        return s
    }

    @Test fun measure_success_returnsNonNegativeMs() = runTest {
        val s = startEchoServer()
        val r = TcpRtt.measure("127.0.0.1", s.localPort, timeoutMs = 1000L)
        assertNotNull(r.getOrNull())
        assertTrue("rtt must be non-negative", r.getOrNull()!! >= 0)
    }

    @Test fun measure_refusedPort_returnsFailure() = runTest {
        // Port 1 is closed on loopback — kernel returns ECONNREFUSED
        // immediately without waiting for the timeout.
        val r = TcpRtt.measure("127.0.0.1", 1, timeoutMs = 500L)
        assertNull(r.getOrNull())
    }
}
