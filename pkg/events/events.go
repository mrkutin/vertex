package events

import (
	"encoding/json"
	"io"
	"log"
	"os"
)

// Emitter outputs structured events for native app wrappers (Swift, Kotlin).
// When enabled, writes JSON lines to stdout. When disabled, uses log.Printf.
type Emitter struct {
	enabled bool
	w       io.Writer
}

// New creates an Emitter. If enabled=true, JSON events go to stdout.
func New(enabled bool) *Emitter {
	return &Emitter{enabled: enabled, w: os.Stdout}
}

// Emit sends an event. With JSON mode: writes to stdout.
// Without JSON mode: logs via log.Printf.
func (e *Emitter) Emit(event string, kv ...string) {
	if !e.enabled {
		if len(kv) == 0 {
			log.Printf("[event] %s", event)
		} else {
			log.Printf("[event] %s %v", event, kvString(kv))
		}
		return
	}

	m := map[string]string{"event": event}
	for i := 0; i+1 < len(kv); i += 2 {
		m[kv[i]] = kv[i+1]
	}
	data, _ := json.Marshal(m)
	data = append(data, '\n')
	e.w.Write(data)
}

func kvString(kv []string) string {
	s := ""
	for i := 0; i+1 < len(kv); i += 2 {
		if s != "" {
			s += " "
		}
		s += kv[i] + "=" + kv[i+1]
	}
	return s
}
