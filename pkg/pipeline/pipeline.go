// Package pipeline provides buffered channel pipelines for VPN data paths.
// Upload (TUN→MQTT) and Download (MQTT→TUN) pipelines decouple I/O from
// processing, preventing blocking on MQTT publish or TUN write.
package pipeline

import (
	"io"
	"log"
	"sync"
	"sync/atomic"

	"github.com/mrkutin/vertex/pkg/vpn"
)

// UploadPipeline reads packets from TUN and passes them through a buffered
// channel to a publisher goroutine that processes and publishes to MQTT.
// Blocking send provides backpressure matching kernel TUN queue behavior.
type UploadPipeline struct {
	ch     chan []byte
	done   chan struct{}
	wg     sync.WaitGroup
	closer io.Closer // TUN closer to unblock reader on Stop
}

// UploadConfig configures the upload pipeline.
type UploadConfig struct {
	ChanSize    int                       // channel buffer size (default 256)
	ReadFunc    func([]byte) (int, error) // tun.Read
	ProcessFunc func([]byte)             // checksums + encrypt + publish
	OnRead      func(int)                // optional: called after successful read with byte count
	Closer      io.Closer                // TUN closer — Stop() calls Close() to unblock ReadFunc
}

// NewUpload creates and starts an upload pipeline with a reader and publisher goroutine.
func NewUpload(cfg UploadConfig) *UploadPipeline {
	if cfg.ChanSize <= 0 {
		cfg.ChanSize = 256
	}
	p := &UploadPipeline{
		ch:     make(chan []byte, cfg.ChanSize),
		done:   make(chan struct{}),
		closer: cfg.Closer,
	}
	p.wg.Add(2)
	go p.reader(cfg.ReadFunc, cfg.OnRead)
	go p.publisher(cfg.ProcessFunc)
	return p
}

func (p *UploadPipeline) reader(readFunc func([]byte) (int, error), onRead func(int)) {
	defer p.wg.Done()
	defer close(p.ch)
	buf := make([]byte, vpn.MTU+100)
	for {
		n, err := readFunc(buf)
		if err != nil {
			select {
			case <-p.done:
				return
			default:
			}
			log.Printf("TUN read error: %v", err)
			return
		}
		if n == 0 {
			continue
		}
		pkt := make([]byte, n)
		copy(pkt, buf[:n])
		if onRead != nil {
			onRead(n)
		}
		select {
		case p.ch <- pkt:
		case <-p.done:
			return
		}
	}
}

func (p *UploadPipeline) publisher(processFunc func([]byte)) {
	defer p.wg.Done()
	for pkt := range p.ch {
		processFunc(pkt)
	}
}

// Stop closes the TUN to unblock the reader, then waits for both goroutines.
func (p *UploadPipeline) Stop() {
	close(p.done)
	if p.closer != nil {
		p.closer.Close() // unblocks readFunc syscall
	}
	p.wg.Wait()
}

// DownloadPipeline receives packets from MQTT callbacks via non-blocking
// Enqueue and writes them to TUN through a single writer goroutine.
type DownloadPipeline struct {
	ch      chan []byte
	done    chan struct{}
	stopped atomic.Bool
	wg      sync.WaitGroup
	drops   atomic.Uint64
}

// DownloadConfig configures the download pipeline.
type DownloadConfig struct {
	ChanSize  int                       // channel buffer size (default 256)
	WriteFunc func([]byte) (int, error) // tun.Write (may include counters/logging)
}

// NewDownload creates and starts a download pipeline with a single writer goroutine.
func NewDownload(cfg DownloadConfig) *DownloadPipeline {
	if cfg.ChanSize <= 0 {
		cfg.ChanSize = 256
	}
	p := &DownloadPipeline{
		ch:   make(chan []byte, cfg.ChanSize),
		done: make(chan struct{}),
	}
	p.wg.Add(1)
	go p.writer(cfg.WriteFunc)
	return p
}

// Enqueue adds a packet to the download pipeline. Non-blocking: returns false
// and increments drop counter if channel is full or pipeline is stopped.
// Safe for concurrent callers (multiple MQTT broker handlers).
func (p *DownloadPipeline) Enqueue(data []byte) bool {
	if p.stopped.Load() {
		return false
	}
	select {
	case p.ch <- data:
		return true
	default:
		p.drops.Add(1)
		return false
	}
}

// Drops returns the number of packets dropped due to channel overflow.
func (p *DownloadPipeline) Drops() uint64 {
	return p.drops.Load()
}

func (p *DownloadPipeline) writer(writeFunc func([]byte) (int, error)) {
	defer p.wg.Done()
	for data := range p.ch {
		writeFunc(data)
	}
}

// Stop prevents new Enqueue calls, closes the channel, and waits for the
// writer to drain all remaining packets.
func (p *DownloadPipeline) Stop() {
	p.stopped.Store(true) // reject new Enqueue calls
	close(p.done)
	close(p.ch) // writer drains via for-range, then exits
	p.wg.Wait()
}
