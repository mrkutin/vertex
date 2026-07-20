package crypto

import (
	"bytes"
	"crypto/rand"
	"fmt"
	"testing"
)

func TestDHEndToEnd(t *testing.T) {
	// Exit has a static keypair
	exitPriv, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	// Client generates ephemeral keypair
	clientPriv, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}

	clientPub := clientPriv.PublicKey().Bytes()
	exitPub := exitPriv.PublicKey().Bytes()

	// Both sides derive session cipher independently
	clientCipher, err := DeriveSessionCipher(clientPriv, exitPriv.PublicKey(), clientPub, exitPub)
	if err != nil {
		t.Fatalf("client derive: %v", err)
	}
	exitCipher, err := DeriveSessionCipher(exitPriv, clientPriv.PublicKey(), clientPub, exitPub)
	if err != nil {
		t.Fatalf("exit derive: %v", err)
	}

	// Client encrypts, exit decrypts
	plaintext := make([]byte, 1500)
	rand.Read(plaintext)

	ct, err := clientCipher.Seal(plaintext)
	if err != nil {
		t.Fatalf("Seal: %v", err)
	}
	pt, err := exitCipher.Open(ct)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	if !bytes.Equal(pt, plaintext) {
		t.Fatal("plaintext mismatch")
	}

	// Exit encrypts, client decrypts
	ct2, err := exitCipher.Seal(plaintext)
	if err != nil {
		t.Fatalf("Seal: %v", err)
	}
	pt2, err := clientCipher.Open(ct2)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	if !bytes.Equal(pt2, plaintext) {
		t.Fatal("plaintext mismatch (reverse)")
	}
}

func TestDHDeterministic(t *testing.T) {
	exitPriv, _ := GenerateKeyPair()
	clientPriv, _ := GenerateKeyPair()

	clientPub := clientPriv.PublicKey().Bytes()
	exitPub := exitPriv.PublicKey().Bytes()

	c1, _ := DeriveSessionCipher(clientPriv, exitPriv.PublicKey(), clientPub, exitPub)
	c2, _ := DeriveSessionCipher(exitPriv, clientPriv.PublicKey(), clientPub, exitPub)

	// Both must produce the same key — verify via cross-encrypt/decrypt
	plain := []byte("test deterministic")
	ct, _ := c1.Seal(plain)
	pt, err := c2.Open(ct)
	if err != nil {
		t.Fatalf("cross-decrypt failed: %v", err)
	}
	if !bytes.Equal(pt, plain) {
		t.Fatal("mismatch")
	}
}

func TestDHDifferentKeys(t *testing.T) {
	// Two different client keypairs must produce different session keys
	exitPriv, _ := GenerateKeyPair()
	client1, _ := GenerateKeyPair()
	client2, _ := GenerateKeyPair()

	exitPub := exitPriv.PublicKey().Bytes()
	c1, _ := DeriveSessionCipher(client1, exitPriv.PublicKey(), client1.PublicKey().Bytes(), exitPub)
	c2, _ := DeriveSessionCipher(client2, exitPriv.PublicKey(), client2.PublicKey().Bytes(), exitPub)

	plain := []byte("should not cross-decrypt")
	ct, _ := c1.Seal(plain)
	if _, err := c2.Open(ct); err == nil {
		t.Fatal("expected error: different clients should have different keys")
	}
}

func TestLoadPrivateKey(t *testing.T) {
	orig, _ := GenerateKeyPair()
	loaded, err := LoadPrivateKey(orig.Bytes())
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(orig.Bytes(), loaded.Bytes()) {
		t.Fatal("loaded key mismatch")
	}
}

func TestEncodePubKeyRoundtrip(t *testing.T) {
	priv, _ := GenerateKeyPair()
	encoded := EncodePubKey(priv.PublicKey())
	decoded, err := DecodePubKey(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(priv.PublicKey().Bytes(), decoded.Bytes()) {
		t.Fatal("pubkey roundtrip mismatch")
	}
}

func BenchmarkDeriveSessionCipher(b *testing.B) {
	exitPriv, _ := GenerateKeyPair()
	clientPriv, _ := GenerateKeyPair()
	clientPub := clientPriv.PublicKey().Bytes()
	exitPub := exitPriv.PublicKey().Bytes()

	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		DeriveSessionCipher(clientPriv, exitPriv.PublicKey(), clientPub, exitPub)
	}
}

func newTestCipher(t testing.TB) *Cipher {
	key := make([]byte, 32)
	if _, err := rand.Read(key); err != nil {
		t.Fatal(err)
	}
	c, err := New(key)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestSealOpen(t *testing.T) {
	c := newTestCipher(t)
	for _, size := range []int{0, 1, 20, 64, 1500} {
		plaintext := make([]byte, size)
		rand.Read(plaintext)

		ct, err := c.Seal(plaintext)
		if err != nil {
			t.Fatalf("Seal(%d): %v", size, err)
		}
		// Ciphertext should be 28 bytes longer
		if len(ct) != size+28 {
			t.Fatalf("Seal(%d): got len %d, want %d", size, len(ct), size+28)
		}

		pt, err := c.Open(ct)
		if err != nil {
			t.Fatalf("Open(%d): %v", size, err)
		}
		if !bytes.Equal(pt, plaintext) {
			t.Fatalf("Open(%d): plaintext mismatch", size)
		}
	}
}

func TestOpenTampered(t *testing.T) {
	c := newTestCipher(t)
	plaintext := make([]byte, 100)
	rand.Read(plaintext)

	ct, _ := c.Seal(plaintext)
	// Flip a byte in the middle of the ciphertext
	ct[len(ct)/2] ^= 0xff

	if _, err := c.Open(ct); err == nil {
		t.Fatal("expected error on tampered ciphertext")
	}
}

func TestOpenWrongKey(t *testing.T) {
	c1 := newTestCipher(t)
	c2 := newTestCipher(t)

	plaintext := make([]byte, 100)
	rand.Read(plaintext)

	ct, _ := c1.Seal(plaintext)
	if _, err := c2.Open(ct); err == nil {
		t.Fatal("expected error with wrong key")
	}
}

func TestOpenTooShort(t *testing.T) {
	c := newTestCipher(t)
	if _, err := c.Open(make([]byte, 27)); err == nil {
		t.Fatal("expected error on short ciphertext")
	}
}

func TestNewBadKey(t *testing.T) {
	if _, err := New(make([]byte, 16)); err == nil {
		t.Fatal("expected error on short key")
	}
}

// --- Benchmarks ---

var benchSizes = []int{64, 128, 256, 512, 1024, 1500}

func BenchmarkSeal(b *testing.B) {
	c := newTestCipher(b)
	for _, size := range benchSizes {
		plaintext := make([]byte, size)
		rand.Read(plaintext)
		b.Run(fmt.Sprintf("%d", size), func(b *testing.B) {
			b.SetBytes(int64(size))
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				c.Seal(plaintext)
			}
		})
	}
}

func BenchmarkOpen(b *testing.B) {
	c := newTestCipher(b)
	for _, size := range benchSizes {
		plaintext := make([]byte, size)
		rand.Read(plaintext)
		ct, _ := c.Seal(plaintext)
		b.Run(fmt.Sprintf("%d", size), func(b *testing.B) {
			b.SetBytes(int64(size))
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				c.Open(ct)
			}
		})
	}
}

func BenchmarkSealOpen(b *testing.B) {
	c := newTestCipher(b)
	for _, size := range benchSizes {
		plaintext := make([]byte, size)
		rand.Read(plaintext)
		b.Run(fmt.Sprintf("%d", size), func(b *testing.B) {
			b.SetBytes(int64(size))
			b.ReportAllocs()
			for i := 0; i < b.N; i++ {
				ct, _ := c.Seal(plaintext)
				c.Open(ct)
			}
		})
	}
}
