package ru.vertices.android.core.protocol

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TopicMatchTest {

    @Test fun exact_match() {
        assertTrue(Topics.matches("vpn/aws/test/in", "vpn/aws/test/in"))
        assertFalse(Topics.matches("vpn/aws/test/in", "vpn/aws/test/out"))
    }

    @Test fun single_level_plus() {
        assertTrue(Topics.matches("vpn/aws/test/in", "vpn/+/test/in"))
        assertTrue(Topics.matches("vpn/sto/test/in", "vpn/+/test/in"))
        assertFalse(Topics.matches("vpn/aws/test/control", "vpn/+/test/in"))
    }

    @Test fun multi_level_hash() {
        assertTrue(Topics.matches("discovery/exits/aws", "discovery/#"))
        assertTrue(Topics.matches("discovery/exits/aws/extra", "discovery/#"))
    }

    @Test fun discovery_wildcard_matches_specific_exit() {
        assertTrue(Topics.matches("discovery/exits/aws", "discovery/exits/+"))
        assertTrue(Topics.matches("discovery/exits/sto", "discovery/exits/+"))
        assertFalse(Topics.matches("discovery/exits", "discovery/exits/+"))
        assertFalse(Topics.matches("discovery/exits/aws/sub", "discovery/exits/+"))
    }
}
