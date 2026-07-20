package crypto

import (
	"crypto/cipher"
	"crypto/ecdh"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"io"

	"golang.org/x/crypto/chacha20poly1305"
	"golang.org/x/crypto/hkdf"
)

// Cipher wraps ChaCha20-Poly1305 AEAD for VPN packet encryption.
type Cipher struct {
	aead cipher.AEAD
}

// New creates a Cipher from a 32-byte pre-shared key.
func New(key []byte) (*Cipher, error) {
	if len(key) != chacha20poly1305.KeySize {
		return nil, fmt.Errorf("key must be %d bytes, got %d", chacha20poly1305.KeySize, len(key))
	}
	aead, err := chacha20poly1305.New(key)
	if err != nil {
		return nil, err
	}
	return &Cipher{aead: aead}, nil
}

// Seal encrypts plaintext. Returns [12-byte nonce][ciphertext + 16-byte tag].
func (c *Cipher) Seal(plaintext []byte) ([]byte, error) {
	nonce := make([]byte, c.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	// Allocate output: nonce + ciphertext + tag
	out := make([]byte, c.aead.NonceSize()+len(plaintext)+c.aead.Overhead())
	copy(out, nonce)
	c.aead.Seal(out[c.aead.NonceSize():c.aead.NonceSize()], nonce, plaintext, nil)
	return out, nil
}

// Open decrypts ciphertext produced by Seal.
func (c *Cipher) Open(ciphertext []byte) ([]byte, error) {
	nonceSize := c.aead.NonceSize()
	if len(ciphertext) < nonceSize+c.aead.Overhead() {
		return nil, errors.New("ciphertext too short")
	}
	nonce := ciphertext[:nonceSize]
	return c.aead.Open(nil, nonce, ciphertext[nonceSize:], nil)
}

// --- X25519 DH Key Exchange ---

// GenerateKeyPair generates an ephemeral X25519 keypair.
func GenerateKeyPair() (*ecdh.PrivateKey, error) {
	return ecdh.X25519().GenerateKey(rand.Reader)
}

// LoadPrivateKey loads an X25519 private key from raw 32 bytes.
func LoadPrivateKey(raw []byte) (*ecdh.PrivateKey, error) {
	return ecdh.X25519().NewPrivateKey(raw)
}

// DeriveSessionCipher performs X25519 ECDH and derives a ChaCha20-Poly1305 cipher
// via HKDF-SHA256. clientPub and exitPub are the raw 32-byte public keys used
// to build the HKDF salt (deterministic ordering: clientPub || exitPub).
func DeriveSessionCipher(myPriv *ecdh.PrivateKey, theirPub *ecdh.PublicKey, clientPub, exitPub []byte) (*Cipher, error) {
	shared, err := myPriv.ECDH(theirPub)
	if err != nil {
		return nil, fmt.Errorf("ECDH: %w", err)
	}
	salt := make([]byte, 0, 64)
	salt = append(salt, clientPub...)
	salt = append(salt, exitPub...)

	r := hkdf.New(sha256.New, shared, salt, []byte("broker-tunnel-v1"))
	key := make([]byte, 32)
	if _, err := io.ReadFull(r, key); err != nil {
		return nil, fmt.Errorf("HKDF: %w", err)
	}
	return New(key)
}

// EncodePubKey encodes a public key to base64 for JSON transport.
func EncodePubKey(pub *ecdh.PublicKey) string {
	return base64.StdEncoding.EncodeToString(pub.Bytes())
}

// DecodePubKey decodes a base64-encoded X25519 public key.
func DecodePubKey(s string) (*ecdh.PublicKey, error) {
	raw, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		return nil, err
	}
	return ecdh.X25519().NewPublicKey(raw)
}
