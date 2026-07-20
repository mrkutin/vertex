package main

import (
	"testing"
	"time"
)

// TestCleanupReturnsIPToFront verifies that when a client's TUN IP is reclaimed
// after the idle timeout, the IP is prepended (LIFO) — not appended — so the
// next assign hands it back to whichever client connects first. With stable
// client sets (r3s/iphone/mac) this keeps assignments sticky across brief
// disconnects.
func TestCleanupReturnsIPToFront(t *testing.T) {
	cm := newClientMap("10.9.2.0/24")

	// Two clients connect in order — they get .2 and .3 from the front of the pool.
	r3sIP, _ := cm.assign("r3s", 0, nil)
	phoneIP, _ := cm.assign("iphone", 0, nil)
	if r3sIP.String() != "10.9.2.2" {
		t.Fatalf("r3s: want 10.9.2.2, got %s", r3sIP)
	}
	if phoneIP.String() != "10.9.2.3" {
		t.Fatalf("iphone first assign: want 10.9.2.3, got %s", phoneIP)
	}

	// iphone goes idle past the timeout while r3s keeps refreshing.
	idle := 31 * time.Minute
	r3sKey := [4]byte{10, 9, 2, 2}
	r3sEntry := cm.byIP[r3sKey]
	r3sEntry.lastSeen = time.Now()
	cm.byIP[r3sKey] = r3sEntry

	phoneKey := [4]byte{10, 9, 2, 3}
	phoneEntry := cm.byIP[phoneKey]
	phoneEntry.lastSeen = time.Now().Add(-idle)
	cm.byIP[phoneKey] = phoneEntry

	cm.cleanup(30 * time.Minute)

	// iphone's mapping was deleted; .3 should be at the FRONT of the pool now.
	if _, exists := cm.byName["iphone"]; exists {
		t.Fatalf("iphone byName still present after cleanup")
	}
	if cm.pool[0].String() != "10.9.2.3" {
		t.Fatalf("LIFO violated: pool[0]=%s, want 10.9.2.3", cm.pool[0])
	}

	// iphone reconnects → must get .3 back, not .4.
	again, _ := cm.assign("iphone", 0, nil)
	if again.String() != "10.9.2.3" {
		t.Fatalf("iphone reassign: want 10.9.2.3, got %s", again)
	}
}

// TestCleanupSurvivesPoolMixing verifies the LIFO change still works when a
// different client claims a freed IP before the original owner returns —
// expected behavior: original owner gets the next-front IP (not its old one).
func TestCleanupSurvivesPoolMixing(t *testing.T) {
	cm := newClientMap("10.9.2.0/24")

	cm.assign("a", 0, nil) // .2
	cm.assign("b", 0, nil) // .3

	// b idles out
	bKey := [4]byte{10, 9, 2, 3}
	bEntry := cm.byIP[bKey]
	bEntry.lastSeen = time.Now().Add(-31 * time.Minute)
	cm.byIP[bKey] = bEntry
	aKey := [4]byte{10, 9, 2, 2}
	aEntry := cm.byIP[aKey]
	aEntry.lastSeen = time.Now()
	cm.byIP[aKey] = aEntry
	cm.cleanup(30 * time.Minute)

	// New client c connects first — grabs the .3 b just freed (LIFO).
	cIP, _ := cm.assign("c", 0, nil)
	if cIP.String() != "10.9.2.3" {
		t.Fatalf("c should claim freed front: want 10.9.2.3, got %s", cIP)
	}
	// b returns — gets .4 (the next front).
	bIP, _ := cm.assign("b", 0, nil)
	if bIP.String() != "10.9.2.4" {
		t.Fatalf("b after c: want 10.9.2.4, got %s", bIP)
	}
}
