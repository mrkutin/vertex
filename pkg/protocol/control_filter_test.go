package protocol

import "testing"

// TestShouldAcceptControl_RegressionRaceBug locks in the fix for the
// 2026-05-19 exit-switch race bug. Before the fix, the gateway/cli control
// handler used:
//
//	if topicExit != s.exitID && (target == nil || topicExit != *target) { return }
//
// which accepts BOTH the old session's keepalive AND the new target's
// response during a switch. The old keepalive could race-win, leaving TUN
// configured with stale IP.
//
// The fix is a mutually-exclusive filter: during a switch only the target,
// in steady state only the current session.
func TestShouldAcceptControl(t *testing.T) {
	cases := []struct {
		name          string
		topicExit     string
		sessionExitID string
		switchTarget  string
		want          bool
	}{
		// Steady state — no switch in progress.
		{"steady: own keepalive accepted", "ams", "ams", "", true},
		{"steady: foreign keepalive dropped", "sto", "ams", "", false},

		// Switch in progress — REGRESSION cases.
		// This is the bug class — old exit's keepalive must be REJECTED even
		// though it matches the still-current session.exitID, because we are
		// waiting for the new target's response.
		{"switch: target response accepted", "sto", "ams", "sto", true},
		{"switch: old exit keepalive REJECTED (the bug)", "ams", "ams", "sto", false},
		{"switch: foreign exit dropped", "aws", "ams", "sto", false},

		// Edge: target equals current (no-op switch). Both filters agree.
		{"switch to same exit (no-op): accepted", "ams", "ams", "ams", true},
		{"switch to same exit: foreign still rejected", "sto", "ams", "ams", false},

		// Defensive: malformed topic (empty exit ID) always rejected.
		{"empty topicExit (malformed)", "", "ams", "", false},
		{"empty topicExit during switch", "", "ams", "sto", false},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := ShouldAcceptControl(c.topicExit, c.sessionExitID, c.switchTarget)
			if got != c.want {
				t.Errorf("ShouldAcceptControl(topicExit=%q, session=%q, target=%q) = %v, want %v",
					c.topicExit, c.sessionExitID, c.switchTarget, got, c.want)
			}
		})
	}
}
