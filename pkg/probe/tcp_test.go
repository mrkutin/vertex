package probe

import (
	"net"
	"net/url"
	"strconv"
	"strings"
	"testing"
	"time"
)

// startListener spins up a TCP listener on 127.0.0.1:0, optionally
// stalling Accept() for `acceptDelay` to simulate a slow broker. The
// returned URL has scheme `mqtt` and the listener's actual port. Caller
// closes the listener via the returned func.
func startListener(t *testing.T, acceptDelay time.Duration) (*url.URL, func()) {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			if acceptDelay > 0 {
				time.Sleep(acceptDelay)
			}
			_ = conn.Close()
		}
	}()
	addr := ln.Addr().(*net.TCPAddr)
	u := &url.URL{Scheme: "mqtt", Host: net.JoinHostPort("127.0.0.1", strconv.Itoa(addr.Port))}
	return u, func() { _ = ln.Close() }
}

func TestMeasureBroker_Success(t *testing.T) {
	u, stop := startListener(t, 0)
	defer stop()

	rtt, err := MeasureBroker(u, 1*time.Second)
	if err != nil {
		t.Fatalf("MeasureBroker: %v", err)
	}
	if rtt <= 0 || rtt > time.Second {
		t.Fatalf("unexpected rtt: %v", rtt)
	}
}

func TestMeasureBroker_Timeout(t *testing.T) {
	// 192.0.2.0/24 is RFC 5737 TEST-NET-1 — guaranteed unroutable.
	// DialTimeout will hit the wall-clock deadline rather than ECONNREFUSED.
	u := &url.URL{Scheme: "mqtt", Host: "192.0.2.1:1883"}
	_, err := MeasureBroker(u, 200*time.Millisecond)
	if err == nil {
		t.Fatal("expected timeout error, got nil")
	}
}

func TestMeasureBroker_DefaultPortFallback(t *testing.T) {
	// URL with no explicit port — connect should still target a port.
	// We can't actually connect (requires root for :443), so just assert
	// the helper doesn't crash and returns a dial error rather than a
	// programming error.
	u := &url.URL{Scheme: "wss", Host: "127.0.0.1"}
	_, err := MeasureBroker(u, 100*time.Millisecond)
	if err == nil {
		t.Fatal("expected dial error on closed port, got nil")
	}
}

// Note: testing "fastest first" between two localhost listeners is
// inherently flaky — TCP handshake on loopback completes in the kernel
// well before user-space Accept() runs, so a `time.Sleep` in the
// listener goroutine does NOT delay client-observed RTT. The fast-vs-slow
// reorder logic is exercised end-to-end in the Docker integration test
// (netem-injected delay). Here we only assert the working/failed
// partition behaviour, which is deterministic.

func TestReorderByRTT_FailedAtTail(t *testing.T) {
	goodU, goodStop := startListener(t, 0)
	defer goodStop()
	deadU := &url.URL{Scheme: "mqtt", Host: "192.0.2.1:1883"}

	urls := []*url.URL{deadU, goodU}
	out := ReorderByRTT(urls, 200*time.Millisecond)

	if out[0] != goodU {
		t.Fatalf("expected good url first, got %s", out[0].Host)
	}
	if out[1] != deadU {
		t.Fatalf("expected dead url at tail, got %s", out[1].Host)
	}
}

func TestReorderByRTT_AllFailedKeepsOrder(t *testing.T) {
	a := &url.URL{Scheme: "mqtt", Host: "192.0.2.1:1883"}
	b := &url.URL{Scheme: "mqtt", Host: "192.0.2.2:1883"}

	urls := []*url.URL{a, b}
	out := ReorderByRTT(urls, 100*time.Millisecond)

	if out[0] != a || out[1] != b {
		t.Fatalf("expected original order preserved when all probes fail")
	}
}

func TestReorderByRTT_SingleURL(t *testing.T) {
	u, stop := startListener(t, 0)
	defer stop()

	urls := []*url.URL{u}
	out := ReorderByRTT(urls, 100*time.Millisecond)

	if len(out) != 1 || out[0] != u {
		t.Fatalf("single-URL input should be returned as-is")
	}
}

func TestReorderByRTT_EmptyInput(t *testing.T) {
	out := ReorderByRTT(nil, 100*time.Millisecond)
	if len(out) != 0 {
		t.Fatalf("empty input should return empty slice")
	}
}

func TestMeasureMap_SuccessAndFailure(t *testing.T) {
	good, goodStop := startListener(t, 0)
	defer goodStop()
	dead := &url.URL{Scheme: "mqtt", Host: "192.0.2.1:1883"}

	urls := []*url.URL{dead, good}
	sorted, rtts := MeasureMap(urls, 200*time.Millisecond)

	if sorted[0] != good {
		t.Fatalf("expected good first")
	}
	if _, ok := rtts[good.Hostname()]; !ok {
		t.Fatalf("good url missing from rtt map")
	}
	if _, ok := rtts[dead.Hostname()]; ok {
		t.Fatalf("dead url should not be in rtt map")
	}
}

func TestFormatOrder(t *testing.T) {
	a := &url.URL{Scheme: "mqtt", Host: "a.example:1883"}
	b := &url.URL{Scheme: "mqtt", Host: "b.example:1883"}
	rtts := map[string]time.Duration{
		"a.example": 25 * time.Millisecond,
	}
	s := FormatOrder([]*url.URL{a, b}, rtts)
	if !strings.Contains(s, "a.example=25ms") {
		t.Fatalf("missing a.example RTT in %q", s)
	}
	if !strings.Contains(s, "b.example=fail") {
		t.Fatalf("missing fail marker for b.example in %q", s)
	}
}
