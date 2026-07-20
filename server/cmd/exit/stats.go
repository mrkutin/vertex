package main

import (
	"encoding/json"
	"fmt"
	"log"
	"os"
	"time"
)

type periodStats struct {
	BytesIn    uint64 `json:"bytesIn"`
	BytesInH   string `json:"bytesInH"`
	BytesOut   uint64 `json:"bytesOut"`
	BytesOutH  string `json:"bytesOutH"`
	PacketsIn  uint64 `json:"packetsIn"`
	PacketsOut uint64 `json:"packetsOut"`
}

type clientStats struct {
	BytesIn    uint64      `json:"bytesIn"`
	BytesInH   string      `json:"bytesInH"`
	BytesOut   uint64      `json:"bytesOut"`
	BytesOutH  string      `json:"bytesOutH"`
	PacketsIn  uint64      `json:"packetsIn"`
	PacketsOut uint64      `json:"packetsOut"`
	LastSeen   time.Time   `json:"lastSeen"`
	Today      periodStats `json:"today"`
	Month      periodStats `json:"month"`
}

type statsFile struct {
	Exit    string                  `json:"exit"`
	Updated time.Time               `json:"updated"`
	Day     int                     `json:"day"`
	Month   int                     `json:"month"`
	Clients map[string]*clientStats `json:"clients"`
}

type statsManager struct {
	file    string
	exitID  string
	data    *statsFile
	prevSnap map[string]clientEntry // previous snapshot for delta calculation
}

func newStatsManager(file, exitID string) *statsManager {
	sm := &statsManager{
		file:     file,
		exitID:   exitID,
		prevSnap: make(map[string]clientEntry),
	}
	sm.load()
	return sm
}

func (sm *statsManager) load() {
	sm.data = &statsFile{
		Exit:    sm.exitID,
		Clients: make(map[string]*clientStats),
	}
	raw, err := os.ReadFile(sm.file)
	if err != nil {
		return
	}
	var loaded statsFile
	if err := json.Unmarshal(raw, &loaded); err != nil {
		return
	}
	if loaded.Clients == nil {
		loaded.Clients = make(map[string]*clientStats)
	}
	sm.data = &loaded
	log.Printf("Stats loaded from %s (%d clients)", sm.file, len(loaded.Clients))
}

func (sm *statsManager) update(snap map[string]clientEntry) {
	now := time.Now().UTC()
	today := now.YearDay()
	month := int(now.Month())

	// Reset periods if day/month changed
	if sm.data.Day != 0 && sm.data.Day != today {
		for _, cs := range sm.data.Clients {
			cs.Today = periodStats{}
		}
	}
	if sm.data.Month != 0 && sm.data.Month != month {
		for _, cs := range sm.data.Clients {
			cs.Month = periodStats{}
		}
	}
	sm.data.Day = today
	sm.data.Month = month

	for name, entry := range snap {
		cs := sm.data.Clients[name]
		if cs == nil {
			cs = &clientStats{}
			sm.data.Clients[name] = cs
		}

		// Calculate delta from previous snapshot
		prev := sm.prevSnap[name]
		deltaIn := entry.bytesIn - prev.bytesIn
		deltaOut := entry.bytesOut - prev.bytesOut
		deltaPktIn := entry.packetsIn - prev.packetsIn
		deltaPktOut := entry.packetsOut - prev.packetsOut

		// Update totals
		cs.BytesIn += deltaIn
		cs.BytesOut += deltaOut
		cs.PacketsIn += deltaPktIn
		cs.PacketsOut += deltaPktOut
		cs.LastSeen = entry.lastSeen

		// Update periods
		cs.Today.BytesIn += deltaIn
		cs.Today.BytesOut += deltaOut
		cs.Today.PacketsIn += deltaPktIn
		cs.Today.PacketsOut += deltaPktOut
		cs.Month.BytesIn += deltaIn
		cs.Month.BytesOut += deltaOut
		cs.Month.PacketsIn += deltaPktIn
		cs.Month.PacketsOut += deltaPktOut

		// Human-readable
		cs.BytesInH = humanBytes(cs.BytesIn)
		cs.BytesOutH = humanBytes(cs.BytesOut)
		cs.Today.BytesInH = humanBytes(cs.Today.BytesIn)
		cs.Today.BytesOutH = humanBytes(cs.Today.BytesOut)
		cs.Month.BytesInH = humanBytes(cs.Month.BytesIn)
		cs.Month.BytesOutH = humanBytes(cs.Month.BytesOut)
	}

	// Save previous snapshot for next delta
	sm.prevSnap = snap
}

func (sm *statsManager) save() {
	sm.data.Updated = time.Now().UTC()
	raw, err := json.MarshalIndent(sm.data, "", "  ")
	if err != nil {
		log.Printf("Stats marshal error: %v", err)
		return
	}
	if err := os.WriteFile(sm.file+".tmp", raw, 0644); err != nil {
		log.Printf("Stats write error: %v", err)
		return
	}
	os.Rename(sm.file+".tmp", sm.file)
}

func humanBytes(b uint64) string {
	switch {
	case b >= 1<<40:
		return fmt.Sprintf("%.2f TB", float64(b)/float64(1<<40))
	case b >= 1<<30:
		return fmt.Sprintf("%.2f GB", float64(b)/float64(1<<30))
	case b >= 1<<20:
		return fmt.Sprintf("%.2f MB", float64(b)/float64(1<<20))
	case b >= 1<<10:
		return fmt.Sprintf("%.1f KB", float64(b)/float64(1<<10))
	default:
		return fmt.Sprintf("%d B", b)
	}
}
