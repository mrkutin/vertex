package ru.vertices.android.core.mqtt

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.vertices.android.core.config.BrokerUrl
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * Regression coverage for `clients/PENDING_TRANSPORT_FIXES.md` "Bug #1":
 * sticky-promoted broker locked the user out after CONNACK auth rejection
 * because the demotion didn't happen — `start()` retried the same bad
 * broker forever even when a healthy backup was sitting at index 1.
 *
 * Scenario (production-real): YC primary, Sber backup. After a successful
 * day on YC, sticky reconnect promotes YC to brokers[0]. Admin rotates
 * password on YC. User reconnect → CONNACK 0x86 on YC → onAuthFailure
 * fires, shouldReconnect=false. Without the fix the next start() tries
 * YC first again. With the fix YC is demoted to the tail, so the next
 * start() reaches Sber.
 *
 * Tests use the `_testFireAuthFailureDisconnect` seam to synthesise the
 * MqttConnection.Event.Disconnected event the real listener would
 * deliver — no Mosquitto required.
 */
class MqttTransportBug1Test {

    private val yc   = BrokerUrl.parse("mqtts://yc.example:8883")!!
    private val sber = BrokerUrl.parse("mqtts://sber.example:8883")!!

    @Test
    fun authFailure_demotesStickyBroker_toTheTail() {
        val authFired = AtomicBoolean(false)
        val transport = MqttTransport(
            initialBrokers = listOf(yc, sber),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1",
            onAuthFailure = { _, _ -> authFired.set(true) }
        )

        assertEquals(listOf("yc.example", "sber.example"), transport._testBrokerHosts())

        transport._testFireAuthFailureDisconnect(connackReason = 0x86)

        assertTrue("onAuthFailure must fire on CONNACK 0x86", authFired.get())
        assertEquals(
            "broker that auth-rejected must be demoted to the tail",
            listOf("sber.example", "yc.example"),
            transport._testBrokerHosts()
        )
    }

    @Test
    fun singleBroker_authFailure_isLeftAlone() {
        val transport = MqttTransport(
            initialBrokers = listOf(yc),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1-single",
            onAuthFailure = { _, _ -> /* swallow */ }
        )

        transport._testFireAuthFailureDisconnect(connackReason = 0x86)

        assertEquals(
            "single-broker setup has nowhere to demote — list must stay intact",
            listOf("yc.example"),
            transport._testBrokerHosts()
        )
    }

    @Test
    fun connackReasonZero_doesNotDemote() {
        val authFired = AtomicBoolean(false)
        val transport = MqttTransport(
            initialBrokers = listOf(yc, sber),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1-zero",
            onAuthFailure = { _, _ -> authFired.set(true) }
        )

        transport._testFireAuthFailureDisconnect(connackReason = 0)

        assertFalse("connackReason=0 must NOT escalate as auth failure", authFired.get())
        assertEquals(
            "non-auth disconnect must leave broker order untouched",
            listOf("yc.example", "sber.example"),
            transport._testBrokerHosts()
        )
    }

    @Test
    fun noAuthFailureSubscriber_stillDemotes_kotlinByDesign() {
        // Intentional Kotlin behaviour, NOT a Swift port:
        //   - Swift gates the demotion block on `let onAuth = onAuthFailure`,
        //     so when the host didn't subscribe the whole block is skipped
        //     and the disconnect falls through to scheduleReconnect().
        //   - Kotlin gates only on `rc != null && rc != 0`. Demotion is
        //     pure routing state — it has nothing to do with whether the
        //     host wants to be told. Demote anyway.
        // Tracked in clients/PENDING_TRANSPORT_FIXES.md as a Swift-side
        // follow-up: align Swift to drop the onAuthFailure gate next
        // VertexCore iteration. Until then this divergence is recorded
        // here so a Swift→Kotlin port reviewer doesn't accidentally
        // "fix" the Kotlin guard back to match Swift.
        val transport = MqttTransport(
            initialBrokers = listOf(yc, sber),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1-nocb",
            onAuthFailure = null
        )

        transport._testFireAuthFailureDisconnect(connackReason = 0x86)

        assertEquals(
            "Kotlin demotes regardless of onAuthFailure subscriber",
            listOf("sber.example", "yc.example"),
            transport._testBrokerHosts()
        )
    }

    @Test
    fun authFailureOnNonPrimaryBroker_doesNotDemote() {
        // Sticky reconnect happens on every successful Connected event,
        // promoting whoever just answered to brokers[0]. If the *backup*
        // (currentBrokerIndex == 1) somehow auth-rejects without ever
        // being promoted, the primary at index 0 is innocent — leaving
        // it pinned to the head is correct, demoting it would punish
        // the wrong broker. Only `currentBrokerIndex == 0` (the broker
        // that actually got the rejection) is touched.
        val transport = MqttTransport(
            initialBrokers = listOf(yc, sber),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1-backup",
            onAuthFailure = { _, _ -> }
        )
        transport._testSetCurrentBrokerIndex(1) // pretend we're on Sber as backup

        transport._testFireAuthFailureDisconnect(connackReason = 0x86)

        assertEquals(
            "auth-fail on a non-primary must leave the primary at index 0",
            listOf("yc.example", "sber.example"),
            transport._testBrokerHosts()
        )
    }

    @Test
    fun twoConsecutiveAuthFailures_rotateTheList() {
        val invocations = AtomicInteger(0)
        // Kotlin's onAuthFailure is a `val` (not nullified after invoke,
        // unlike Swift), so a second synthesised event will hit the
        // demotion path again without re-arming.
        val transport = MqttTransport(
            initialBrokers = listOf(yc, sber),
            username = "user",
            password = "stale",
            clientId = "vtx-test-bug1-double",
            onAuthFailure = { _, _ -> invocations.incrementAndGet() }
        )

        // First demote: [YC, Sber] → [Sber, YC]
        transport._testFireAuthFailureDisconnect(connackReason = 0x86)
        assertEquals(listOf("sber.example", "yc.example"), transport._testBrokerHosts())

        // Second demote: [Sber, YC] → [YC, Sber] — rotated back to original.
        transport._testFireAuthFailureDisconnect(connackReason = 0x86)
        assertEquals(
            "two demotions cycle the list and bring the original primary back",
            listOf("yc.example", "sber.example"),
            transport._testBrokerHosts()
        )
        assertEquals("onAuthFailure fired twice", 2, invocations.get())
    }
}
