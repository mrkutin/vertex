// Package discovery accumulates exit-node liveness events into a tracker
// and runs the scoring/selection logic. Events arrive via the abstract
// `liveness.Feed` (today: MQTT retained heartbeats); this package no
// longer parses topics or knows the transport encoding.
package discovery

import (
	"log"
	"math"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/mrkutin/vertex/pkg/liveness"
)

// ExitInfo represents a discovered exit node from its heartbeat.
type ExitInfo struct {
	ID          string            `json:"id"`
	Country     string            `json:"country"`
	Clients     int               `json:"clients"`
	MaxClients  int               `json:"max_clients"`
	BrokerRTTms map[string]int64  `json:"broker_rtt_ms"`
	Uptime      int64             `json:"uptime"`
	TS          int64             `json:"ts"`
	DHPubKey    string            `json:"dh_pubkey,omitempty"` // base64 X25519 public key
	ReceivedAt  time.Time         `json:"-"`                   // local time when heartbeat was received
}

// IsStale returns true if heartbeat is older than maxAge.
func (e *ExitInfo) IsStale(maxAge time.Duration) bool {
	return time.Since(e.ReceivedAt) > maxAge
}

// Tracker tracks discovered exit nodes and selects the best one.
type Tracker struct {
	mu         sync.RWMutex
	exits      map[string]*ExitInfo // keyed by exit ID
	loadFactor float64
	staleAge   time.Duration
}

// NewTracker creates a discovery tracker.
func NewTracker() *Tracker {
	return &Tracker{
		exits:      make(map[string]*ExitInfo),
		loadFactor: 2.0,
		staleAge:   90 * time.Second,
	}
}

// HandleEvent ingests one liveness.NodeEvent. Updated events refresh
// in-memory state; removed events drop the entry. Callers (clients/cli,
// clients/gateway) typically pump a Watch channel into this method.
func (t *Tracker) HandleEvent(evt liveness.NodeEvent) {
	switch evt.Type {
	case liveness.EventRemoved:
		t.mu.Lock()
		delete(t.exits, evt.Info.ID)
		t.mu.Unlock()
		log.Printf("[discovery] exit %s offline", evt.Info.ID)
	case liveness.EventUpdated:
		info := ExitInfo{
			ID:          evt.Info.ID,
			Country:     evt.Info.Country,
			Clients:     evt.Info.Clients,
			MaxClients:  evt.Info.MaxClients,
			BrokerRTTms: evt.Info.BrokerRTTs,
			Uptime:      evt.Info.Uptime,
			TS:          evt.Info.TS,
			DHPubKey:    evt.Info.DHPubKey,
			ReceivedAt:  time.Now(),
		}
		t.mu.Lock()
		t.exits[info.ID] = &info
		t.mu.Unlock()
		log.Printf("[discovery] exit %s: country=%s clients=%d/%d uptime=%ds",
			info.ID, info.Country, info.Clients, info.MaxClients, info.Uptime)
	default:
		// EventUnknown — Feed contract says implementations don't emit it.
	}
}

// Pump runs `for evt := range events { HandleEvent(evt) }` in a goroutine
// and returns immediately. The goroutine exits when the channel closes
// (typically because the caller cancelled the Feed's Watch context).
// Sugar for the standard consumer pattern.
func (t *Tracker) Pump(events <-chan liveness.NodeEvent) {
	go func() {
		for evt := range events {
			t.HandleEvent(evt)
		}
	}()
}

const defaultCapacity = 253 // IP pool size: .2-.254 in /24

// exitScore calculates the selection score for an exit. Lower = better.
// Must be called under t.mu.RLock.
func (t *Tracker) exitScore(info *ExitInfo, brokerHost string) float64 {
	rtt := t.getBrokerRTT(info, brokerHost)
	if rtt <= 0 {
		rtt = 100 // default if RTT unknown
	}
	capacity := float64(defaultCapacity)
	if info.MaxClients > 0 {
		capacity = float64(info.MaxClients)
	}
	return float64(rtt) * (1.0 + float64(info.Clients)/capacity*t.loadFactor)
}

// BestExit selects the best exit for the given broker host.
// Returns exit ID and true, or empty string and false if no exits available.
func (t *Tracker) BestExit(brokerHost string) (string, bool) {
	t.mu.RLock()
	defer t.mu.RUnlock()
	return t.bestExitLocked(brokerHost, "")
}

// bestExitLocked finds the best exit excluding excludeID. Must be called under RLock.
func (t *Tracker) bestExitLocked(brokerHost, excludeID string) (string, bool) {
	type scored struct {
		id    string
		score float64
	}
	var candidates []scored

	for _, info := range t.exits {
		if info.ID == excludeID || info.IsStale(t.staleAge) {
			continue
		}
		if info.MaxClients > 0 && info.Clients >= info.MaxClients {
			continue
		}
		candidates = append(candidates, scored{info.ID, t.exitScore(info, brokerHost)})
	}

	if len(candidates) == 0 {
		return "", false
	}

	sort.Slice(candidates, func(i, j int) bool {
		return candidates[i].score < candidates[j].score
	})

	return candidates[0].id, true
}

// ShouldSwitch returns true if we should switch from currentExit to a better one.
// Uses 1.5x tolerance to prevent flapping.
func (t *Tracker) ShouldSwitch(currentExit, brokerHost string) (string, bool) {
	t.mu.RLock()
	defer t.mu.RUnlock()

	currentInfo, ok := t.exits[currentExit]
	if !ok || currentInfo.IsStale(t.staleAge) {
		// Current exit is gone — find best alternative
		return t.bestExitLocked(brokerHost, currentExit)
	}

	currentScore := t.exitScore(currentInfo, brokerHost)

	// Find best alternative
	var bestID string
	bestScore := math.MaxFloat64
	for _, info := range t.exits {
		if info.ID == currentExit || info.IsStale(t.staleAge) {
			continue
		}
		if info.MaxClients > 0 && info.Clients >= info.MaxClients {
			continue
		}
		score := t.exitScore(info, brokerHost)
		if score < bestScore {
			bestScore = score
			bestID = info.ID
		}
	}

	// Switch only if significantly better (1.5x threshold)
	if bestID != "" && bestScore*1.5 < currentScore {
		return bestID, true
	}
	return "", false
}

// GetExitInfo returns the ExitInfo for the given exit ID, or nil if not found/stale.
func (t *Tracker) GetExitInfo(exitID string) *ExitInfo {
	t.mu.RLock()
	defer t.mu.RUnlock()
	info, ok := t.exits[exitID]
	if !ok || info.IsStale(t.staleAge) {
		return nil
	}
	return info
}

// ExitAvailable returns true if the given exit has a recent heartbeat.
func (t *Tracker) ExitAvailable(exitID string) bool {
	t.mu.RLock()
	defer t.mu.RUnlock()
	info, ok := t.exits[exitID]
	return ok && !info.IsStale(t.staleAge)
}

// List returns all known non-stale exits.
func (t *Tracker) List() []ExitInfo {
	t.mu.RLock()
	defer t.mu.RUnlock()
	var result []ExitInfo
	for _, info := range t.exits {
		if !info.IsStale(t.staleAge) {
			result = append(result, *info)
		}
	}
	return result
}

func (t *Tracker) getBrokerRTT(info *ExitInfo, brokerHost string) int64 {
	if info.BrokerRTTms == nil {
		return 0
	}
	// Direct lookup
	if rtt, ok := info.BrokerRTTms[brokerHost]; ok {
		return rtt
	}
	// Try partial match: strip port from both sides
	bare := stripPort(brokerHost)
	for host, rtt := range info.BrokerRTTms {
		if stripPort(host) == bare {
			return rtt
		}
	}
	return 0
}

func stripPort(host string) string {
	if i := strings.LastIndex(host, ":"); i > 0 {
		return host[:i]
	}
	return host
}

