package mqtt

import (
	"testing"

	"github.com/mrkutin/vertex/pkg/transport"
)

// TestMQTTSatisfiesTransport is a compile-time check via type assertion: if
// *Transport ever drifts from transport.Transport / transport.Retainer, this
// test fails to build before any runtime test runs. The same checks exist
// inline in mqtt.go (`var _ transport.Transport = ...`) — duplicated here so
// `go test ./pkg/transport/...` flags interface drift even when callers
// haven't imported the package yet.
func TestMQTTSatisfiesTransport(t *testing.T) {
	var _ transport.Transport = (*Transport)(nil)
	var _ transport.Retainer = (*Transport)(nil)
	var _ transport.RTTProbe = (*Transport)(nil)
}
