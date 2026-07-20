package discovery

import (
	"sync"
	"testing"
	"time"

	"github.com/mrkutin/vertex/pkg/liveness"
)

// addExit is the test analogue of receiving a fresh heartbeat from `id`.
// Wraps NodeInfo construction so individual cases can stay readable.
func addExit(t *Tracker, id, country string, clients, maxClients int, rtt map[string]int64) {
	t.HandleEvent(liveness.NodeEvent{
		Type: liveness.EventUpdated,
		Info: liveness.NodeInfo{
			ID:         id,
			Country:    country,
			Clients:    clients,
			MaxClients: maxClients,
			BrokerRTTs: rtt,
			Uptime:     100,
			TS:         time.Now().Unix(),
		},
	})
}

func removeExit(t *Tracker, id string) {
	t.HandleEvent(liveness.NodeEvent{Type: liveness.EventRemoved, Info: liveness.NodeInfo{ID: id}})
}

func TestHandleEventAdds(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 5, 50, map[string]int64{"broker-ru": 70})

	exits := tr.List()
	if len(exits) != 1 {
		t.Fatalf("expected 1 exit, got %d", len(exits))
	}
	if exits[0].ID != "aws" || exits[0].Country != "CA" || exits[0].Clients != 5 {
		t.Fatalf("unexpected exit: %+v", exits[0])
	}
}

func TestHandleEventRemoves(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 0, 50, nil)
	if len(tr.List()) != 1 {
		t.Fatal("expected 1 exit after add")
	}
	removeExit(tr, "aws")
	if len(tr.List()) != 0 {
		t.Fatal("expected 0 exits after remove")
	}
}

func TestPumpDrainsChannel(t *testing.T) {
	tr := NewTracker()
	ch := make(chan liveness.NodeEvent, 4)
	ch <- liveness.NodeEvent{Type: liveness.EventUpdated, Info: liveness.NodeInfo{ID: "aws"}}
	ch <- liveness.NodeEvent{Type: liveness.EventUpdated, Info: liveness.NodeInfo{ID: "ams"}}
	close(ch)

	tr.Pump(ch)
	// Pump goroutine drains the closed channel — give it a moment.
	deadline := time.After(time.Second)
	for {
		if len(tr.List()) == 2 {
			return
		}
		select {
		case <-deadline:
			t.Fatalf("Pump did not drain channel: %d/2", len(tr.List()))
		default:
			time.Sleep(5 * time.Millisecond)
		}
	}
}

func TestBestExit(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 2, 50, map[string]int64{"broker-ru": 70, "broker-eu": 80})
	addExit(tr, "ams", "NL", 2, 50, map[string]int64{"broker-ru": 50, "broker-eu": 5})

	// From broker-ru: ams has lower RTT (50 < 70)
	best, ok := tr.BestExit("broker-ru")
	if !ok || best != "ams" {
		t.Fatalf("expected ams from broker-ru, got %s", best)
	}

	// From broker-eu: ams has much lower RTT (5 < 80)
	best, ok = tr.BestExit("broker-eu")
	if !ok || best != "ams" {
		t.Fatalf("expected ams from broker-eu, got %s", best)
	}
}

func TestBestExitLoadBalancing(t *testing.T) {
	tr := NewTracker()
	// Same RTT, different load
	addExit(tr, "eu1", "NL", 40, 50, map[string]int64{"broker": 5})
	addExit(tr, "eu2", "DE", 5, 50, map[string]int64{"broker": 6})

	// eu2 should win: 6*(1+5/50*2)=7.2 < eu1: 5*(1+40/50*2)=13
	best, ok := tr.BestExit("broker")
	if !ok || best != "eu2" {
		t.Fatalf("expected eu2 (less loaded), got %s", best)
	}
}

func TestBestExitSkipsFull(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 50, 50, map[string]int64{"broker": 10})
	addExit(tr, "ams", "NL", 5, 50, map[string]int64{"broker": 100})

	// aws is full (clients >= max_clients), should pick ams
	best, ok := tr.BestExit("broker")
	if !ok || best != "ams" {
		t.Fatalf("expected ams (aws is full), got %s", best)
	}
}

func TestBestExitSkipsStale(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 0, 50, map[string]int64{"broker": 10})

	// Make it stale
	tr.mu.Lock()
	info := tr.exits["aws"]
	info.ReceivedAt = time.Now().Add(-2 * time.Minute)
	tr.mu.Unlock()

	_, ok := tr.BestExit("broker")
	if ok {
		t.Fatal("stale exit should not be selected")
	}
}

func TestBestExitNoExits(t *testing.T) {
	tr := NewTracker()
	_, ok := tr.BestExit("broker")
	if ok {
		t.Fatal("empty tracker should return false")
	}
}

func TestShouldSwitchStaleCurrent(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 0, 50, map[string]int64{"broker": 10})
	addExit(tr, "ams", "NL", 0, 50, map[string]int64{"broker": 5})

	// Make aws stale
	tr.mu.Lock()
	tr.exits["aws"].ReceivedAt = time.Now().Add(-2 * time.Minute)
	tr.mu.Unlock()

	better, shouldSwitch := tr.ShouldSwitch("aws", "broker")
	if !shouldSwitch || better != "ams" {
		t.Fatalf("should switch from stale aws to ams, got %s/%v", better, shouldSwitch)
	}
}

func TestShouldSwitchThreshold(t *testing.T) {
	tr := NewTracker()
	// Current: 50ms RTT, alt: 40ms RTT — NOT enough difference (1.5x threshold)
	addExit(tr, "aws", "CA", 0, 50, map[string]int64{"broker": 50})
	addExit(tr, "ams", "NL", 0, 50, map[string]int64{"broker": 40})

	_, shouldSwitch := tr.ShouldSwitch("aws", "broker")
	if shouldSwitch {
		t.Fatal("40ms vs 50ms should NOT trigger switch (1.5x threshold)")
	}

	// Now make ams much better: 10ms vs 50ms
	addExit(tr, "ams", "NL", 0, 50, map[string]int64{"broker": 10})

	better, shouldSwitch := tr.ShouldSwitch("aws", "broker")
	if !shouldSwitch || better != "ams" {
		t.Fatalf("10ms vs 50ms should trigger switch, got %s/%v", better, shouldSwitch)
	}
}

func TestExitAvailable(t *testing.T) {
	tr := NewTracker()
	if tr.ExitAvailable("aws") {
		t.Fatal("nonexistent exit should not be available")
	}

	addExit(tr, "aws", "CA", 0, 50, nil)
	if !tr.ExitAvailable("aws") {
		t.Fatal("fresh exit should be available")
	}
}

func TestBrokerRTTHostNormalization(t *testing.T) {
	tr := NewTracker()
	addExit(tr, "aws", "CA", 0, 50, map[string]int64{"mqtt.example.com": 50})

	// Should match with port stripped
	best, ok := tr.BestExit("mqtt.example.com:8883")
	if !ok || best != "aws" {
		t.Fatalf("expected aws with host normalization, got %s/%v", best, ok)
	}
}

func TestConcurrentAccess(t *testing.T) {
	tr := NewTracker()
	var wg sync.WaitGroup

	// Concurrent updates
	for i := 0; i < 10; i++ {
		wg.Add(1)
		go func(id int) {
			defer wg.Done()
			for j := 0; j < 100; j++ {
				addExit(tr, "exit", "US", j, 50, map[string]int64{"broker": 10})
			}
		}(i)
	}

	// Concurrent reads
	for i := 0; i < 5; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := 0; j < 100; j++ {
				tr.BestExit("broker")
				tr.ExitAvailable("exit")
				tr.ShouldSwitch("exit", "broker")
				tr.List()
			}
		}()
	}

	wg.Wait()
}
