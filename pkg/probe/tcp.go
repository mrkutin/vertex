// Package probe provides client-side network probes used to rank
// candidate brokers (or other endpoints) before opening a long-lived
// connection. Today the only probe is a TCP-connect-time RTT
// measurement; future additions might include UDP path-MTU or HTTP
// HEAD-time probes.
//
// The package is intentionally transport-agnostic — it does not import
// `pkg/transport` or any MQTT client library — so it can be reused by
// CLI clients, gateway agents, and any future probe-driven scheduling.
package probe

import (
	"net"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"
)

// MeasureBroker measures TCP-connect time to the broker URL's host:port.
// Returns the elapsed duration on success, or (0, err) on dial failure
// or timeout. Only the TCP handshake is timed (SYN → SYN+ACK); TLS and
// WebSocket negotiation are excluded so a slow certificate chain does
// not skew the network-latency reading.
//
// `wss://host:443/path` and `mqtts://host:8883` collapse to plain
// `host:port` — `path` is irrelevant for a connect probe.
func MeasureBroker(u *url.URL, timeout time.Duration) (time.Duration, error) {
	addr := u.Host
	if _, _, err := net.SplitHostPort(addr); err != nil {
		// URL had no explicit port — fall back to scheme-default ports.
		// `net.JoinHostPort` handles IPv6 brackets correctly.
		port := defaultPort(u.Scheme)
		addr = net.JoinHostPort(u.Hostname(), port)
	}
	start := time.Now()
	conn, err := net.DialTimeout("tcp", addr, timeout)
	if err != nil {
		return 0, err
	}
	rtt := time.Since(start)
	_ = conn.Close()
	return rtt, nil
}

// ReorderByRTT probes all URLs in parallel and returns them sorted
// ascending by RTT. Failed probes (timeout or refusal) are appended
// after the successful ones, preserving their original relative order
// — so a degraded broker still gets tried as a fallback rather than
// being silently dropped.
//
// Always returns a slice of the same length as the input. Empty or
// single-URL input is a no-op.
func ReorderByRTT(urls []*url.URL, timeout time.Duration) []*url.URL {
	sorted, _ := MeasureMap(urls, timeout)
	return sorted
}

// probeResult is the per-URL output of one TCP-connect probe.
type probeResult struct {
	idx int
	rtt time.Duration
	ok  bool
}

// sortByRTT sorts in place: successful probes ascending by RTT (with
// original index as tiebreaker for stability), then failed probes in
// their original order. Single source of truth — both `ReorderByRTT`
// and `MeasureMap` go through this so the comparator can't drift.
func sortByRTT(results []probeResult) {
	sort.SliceStable(results, func(a, b int) bool {
		if results[a].ok != results[b].ok {
			return results[a].ok
		}
		if !results[a].ok {
			return results[a].idx < results[b].idx
		}
		if results[a].rtt != results[b].rtt {
			return results[a].rtt < results[b].rtt
		}
		return results[a].idx < results[b].idx
	})
}

// FormatOrder produces a human-readable "host=Xms host=Yms" string for
// logging. Failures show as `host=fail`. Useful when emitting a single
// concise log line about the resulting probe order.
func FormatOrder(urls []*url.URL, rtts map[string]time.Duration) string {
	parts := make([]string, 0, len(urls))
	for _, u := range urls {
		host := u.Hostname()
		if d, ok := rtts[host]; ok {
			parts = append(parts, host+"="+d.Round(time.Millisecond).String())
		} else {
			parts = append(parts, host+"=fail")
		}
	}
	return strings.Join(parts, " ")
}

// MeasureMap is the underlying parallel probe used by [ReorderByRTT];
// exposed separately so callers (today: CLI / gateway) can also log the
// raw RTT map alongside the reordered list. When multiple URLs share a
// hostname (e.g. `mqtts://host:8883` and `wss://host:443`), the lower
// RTT wins for that host.
func MeasureMap(urls []*url.URL, timeout time.Duration) (sorted []*url.URL, rtts map[string]time.Duration) {
	rtts = make(map[string]time.Duration, len(urls))
	if len(urls) == 0 {
		return nil, rtts
	}
	if len(urls) == 1 {
		// Still measure once — callers may want to surface latency
		// even when there's no reordering decision to make.
		if d, err := MeasureBroker(urls[0], timeout); err == nil {
			rtts[urls[0].Hostname()] = d
		}
		return urls, rtts
	}

	results := make([]probeResult, len(urls))

	var wg sync.WaitGroup
	var mu sync.Mutex
	wg.Add(len(urls))
	for i, u := range urls {
		go func(i int, u *url.URL) {
			defer wg.Done()
			rtt, err := MeasureBroker(u, timeout)
			results[i] = probeResult{idx: i, rtt: rtt, ok: err == nil}
			if err == nil {
				mu.Lock()
				// Per-host display map: keep the lower of the two RTTs
				// when the same host appears with multiple schemes
				// (e.g. mqtts://yc:8883 + wss://yc:443). On exact tie
				// the first to land wins — goroutine completion order
				// is not deterministic, but this map is diagnostic
				// only (used for log lines) and does NOT affect the
				// returned ordering.
				if existing, seen := rtts[u.Hostname()]; !seen || rtt < existing {
					rtts[u.Hostname()] = rtt
				}
				mu.Unlock()
			}
		}(i, u)
	}
	wg.Wait()

	sortByRTT(results)

	sorted = make([]*url.URL, len(urls))
	for i, r := range results {
		sorted[i] = urls[r.idx]
	}
	return sorted, rtts
}

// defaultPort returns the IANA / convention default for a given URL
// scheme. Non-exhaustive — mirrors the schemes used by Vertex brokers.
func defaultPort(scheme string) string {
	switch scheme {
	case "mqtts", "ssl", "tls":
		return "8883"
	case "wss":
		return "443"
	case "ws":
		return "80"
	case "mqtt", "tcp", "":
		return "1883"
	}
	return "1883"
}

