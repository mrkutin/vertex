package protocol

import "testing"

// TestRoundTripMQTT exercises every kind we encode: build, encode to MQTT
// topic, parse back, and compare. ParseMQTTTopic is the inverse of
// MQTTTopic for every well-formed address — that's the contract callers
// rely on when they replace `strings.Split(topic, "/")` with a parse call.
func TestRoundTripMQTT(t *testing.T) {
	cases := []Address{
		DataOut("aws", "iphone"),
		DataIn("aws", "iphone"),
		Control("sto", "r3s"),
		Join("aws"),
		DiscoveryHeartbeat("sto"),
		DiscoveryPing("aws", 2),
	}
	for _, want := range cases {
		t.Run(want.Kind.String(), func(t *testing.T) {
			topic := want.MQTTTopic()
			if topic == "" {
				t.Fatalf("MQTTTopic returned empty for %+v", want)
			}
			got := ParseMQTTTopic(topic)
			if got != want {
				t.Errorf("round-trip mismatch:\n  topic: %s\n  want : %+v\n  got  : %+v", topic, want, got)
			}
		})
	}
}

// TestParseDistinguishesJoinFromControl is the easy bug in the layout:
// "vpn/{exit}/control/join" has the literal "control" in the segment that
// usually holds the client name, and "vpn/{exit}/{name}/control" has the
// literal "control" in the segment that usually holds the message kind.
// A naive parser confuses the two.
func TestParseDistinguishesJoinFromControl(t *testing.T) {
	t.Run("join", func(t *testing.T) {
		got := ParseMQTTTopic("vpn/aws/control/join")
		want := Join("aws")
		if got != want {
			t.Errorf("want %+v, got %+v", want, got)
		}
	})
	t.Run("control to client named control", func(t *testing.T) {
		// Pathological but valid: a client named "control" addressed by
		// the exit. The 4th segment ("control") forces the control branch.
		got := ParseMQTTTopic("vpn/aws/control/control")
		want := Control("aws", "control")
		if got != want {
			t.Errorf("want %+v, got %+v", want, got)
		}
	})
}

func TestParseRejectsUnknown(t *testing.T) {
	cases := []string{
		"",
		"random",
		"vpn/aws",                       // too few segments
		"vpn/aws/iphone/out/extra",      // too many
		"vpn/aws/iphone/garbage",        // unknown 4th segment
		"discovery/ping/aws/notnumeric", // non-numeric index
		"other/aws/iphone/out",          // wrong namespace
	}
	for _, topic := range cases {
		t.Run(topic, func(t *testing.T) {
			got := ParseMQTTTopic(topic)
			if got.Kind != KindUnknown {
				t.Errorf("expected KindUnknown for %q, got %+v", topic, got)
			}
		})
	}
}

// TestSubscriptionPatterns verifies the wildcard helpers don't drift —
// these strings are baked into Mosquitto ACLs on production brokers, so
// breaking them silently would lock clients out.
func TestSubscriptionPatterns(t *testing.T) {
	cases := []struct {
		got  string
		want string
	}{
		{MQTTDataInbound("aws"), "vpn/aws/+/out"},
		{MQTTControlAny("iphone"), "vpn/+/iphone/control"},
		{MQTTDataAny("iphone"), "vpn/+/iphone/in"},
		{MQTTJoinPattern("aws"), "vpn/aws/control/join"},
		{MQTTDiscoveryAll, "discovery/exits/+"},
	}
	for _, c := range cases {
		if c.got != c.want {
			t.Errorf("pattern drift: want %q, got %q", c.want, c.got)
		}
	}
}
