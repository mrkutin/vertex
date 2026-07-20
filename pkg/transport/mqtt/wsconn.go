package mqtt

import (
	"context"
	"crypto/tls"
	"io"
	"net"
	"net/http"
	"net/url"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

// wsConn wraps a gorilla/websocket.Conn to satisfy net.Conn.
//
// MQTT over WebSocket requires each MQTT Control Packet to be sent as a single
// WebSocket binary message. paho.golang uses net.Buffers.WriteTo which calls
// Write() multiple times per MQTT packet. Over TCP this is fine (byte stream),
// but over WebSocket each Write() creates a separate frame → broker sees
// partial packets → "malformed packet" disconnect.
//
// Fix: Lock()/Unlock() (called by paho before/after each packet write) open and
// close a single WebSocket writer, so all Write() calls within one packet go
// into one frame.
type wsConn struct {
	conn   *websocket.Conn
	writer io.WriteCloser
	mu     sync.Mutex
	r      io.Reader
	rio    sync.Mutex
}

// dialWebSocket creates a WebSocket connection for use as an MQTT transport.
func dialWebSocket(ctx context.Context, tlsCfg *tls.Config, serverURL *url.URL) (net.Conn, error) {
	d := *websocket.DefaultDialer
	d.TLSClientConfig = tlsCfg
	d.Subprotocols = []string{"mqtt"}

	ws, _, err := d.DialContext(ctx, serverURL.String(), http.Header{})
	if err != nil {
		return nil, err
	}
	return &wsConn{conn: ws}, nil
}

// Lock opens a new WebSocket binary message writer.
// paho calls Lock() before writing each MQTT packet.
func (c *wsConn) Lock() {
	c.mu.Lock()
	w, err := c.conn.NextWriter(websocket.BinaryMessage)
	if err == nil {
		c.writer = w
	}
}

// Unlock flushes and closes the current WebSocket message, then releases the lock.
// paho calls Unlock() after writing each MQTT packet.
func (c *wsConn) Unlock() {
	if c.writer != nil {
		c.writer.Close()
		c.writer = nil
	}
	c.mu.Unlock()
}

// Write writes data to the current WebSocket message frame.
// Multiple Write calls between Lock/Unlock go into one frame.
func (c *wsConn) Write(p []byte) (int, error) {
	if c.writer != nil {
		return c.writer.Write(p)
	}
	// Fallback: no active writer (outside Lock/Unlock), send as individual message
	err := c.conn.WriteMessage(websocket.BinaryMessage, p)
	if err != nil {
		return 0, err
	}
	return len(p), nil
}

// Read reads from the current WebSocket message, advancing to the next on EOF.
func (c *wsConn) Read(p []byte) (int, error) {
	c.rio.Lock()
	defer c.rio.Unlock()
	for {
		if c.r == nil {
			var err error
			_, c.r, err = c.conn.NextReader()
			if err != nil {
				return 0, err
			}
		}
		n, err := c.r.Read(p)
		if err == io.EOF {
			c.r = nil
			if n > 0 {
				return n, nil
			}
			continue
		}
		return n, err
	}
}

func (c *wsConn) Close() error                       { return c.conn.Close() }
func (c *wsConn) LocalAddr() net.Addr                { return c.conn.LocalAddr() }
func (c *wsConn) RemoteAddr() net.Addr               { return c.conn.RemoteAddr() }
func (c *wsConn) SetDeadline(t time.Time) error      { return c.conn.SetReadDeadline(t) }
func (c *wsConn) SetReadDeadline(t time.Time) error  { return c.conn.SetReadDeadline(t) }
func (c *wsConn) SetWriteDeadline(t time.Time) error { return c.conn.SetWriteDeadline(t) }
