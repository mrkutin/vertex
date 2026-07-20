// Package identity provides device identity keys for vertex.
// Each device gets a persistent X25519 keypair (like WireGuard).
// The exit node maintains a TOFU key store to prevent credential sharing.
package identity

import (
	"crypto/ecdh"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strings"
	"os"
	"path/filepath"
	"sync"
	"time"

	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
)

// LoadOrGenerateKey loads a hex-encoded X25519 private key from path,
// or generates a new one and saves it if the file does not exist.
// Key file is 64 hex characters (32 bytes), permissions 0600.
// Parent directories are created with 0700 if needed.
func LoadOrGenerateKey(path string) (*ecdh.PrivateKey, error) {
	data, err := os.ReadFile(path)
	if err == nil {
		return loadHexKey(data)
	}
	if !os.IsNotExist(err) {
		return nil, fmt.Errorf("read identity key: %w", err)
	}

	// Generate new key.
	priv, err := btcrypto.GenerateKeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate identity key: %w", err)
	}

	// Create parent directories.
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return nil, fmt.Errorf("create key directory: %w", err)
	}

	// Save hex-encoded private key.
	encoded := hex.EncodeToString(priv.Bytes())
	if err := os.WriteFile(path, []byte(encoded), 0600); err != nil {
		return nil, fmt.Errorf("write identity key: %w", err)
	}

	return priv, nil
}

// loadHexKey decodes hex-encoded raw key bytes and loads as X25519 private key.
func loadHexKey(data []byte) (*ecdh.PrivateKey, error) {
	raw, err := hex.DecodeString(strings.TrimSpace(string(data)))
	if err != nil {
		return nil, fmt.Errorf("decode hex key: %w", err)
	}
	priv, err := btcrypto.LoadPrivateKey(raw)
	if err != nil {
		return nil, fmt.Errorf("load identity key: %w", err)
	}
	return priv, nil
}

// Identity HMAC label.
const identityLabelV1 = "vtx-identity-v1"

// ComputeIdentityProof creates an HMAC proof that the client owns
// the given identity private key. The proof binds the identity to
// a specific exit node and client name.
//
// Protocol: HMAC-SHA256(key=ECDH(identityPriv, exitPub), msg=identityLabelV1+name)
func ComputeIdentityProof(identityPriv *ecdh.PrivateKey, exitPub *ecdh.PublicKey, name string) ([]byte, error) {
	shared, err := identityPriv.ECDH(exitPub)
	if err != nil {
		return nil, fmt.Errorf("identity ECDH: %w", err)
	}
	mac := hmac.New(sha256.New, shared)
	mac.Write([]byte(identityLabelV1 + name))
	return mac.Sum(nil), nil
}

// VerifyIdentityProof verifies that proof was produced by the holder
// of the private key corresponding to identityPub.
func VerifyIdentityProof(exitPriv *ecdh.PrivateKey, identityPub *ecdh.PublicKey, name string, proof []byte) bool {
	shared, err := exitPriv.ECDH(identityPub)
	if err != nil {
		return false
	}
	mac := hmac.New(sha256.New, shared)
	mac.Write([]byte(identityLabelV1 + name))
	return hmac.Equal(mac.Sum(nil), proof)
}

// DeviceKey represents a registered device's public key.
type DeviceKey struct {
	PublicKey string    `json:"public_key"`
	Added     time.Time `json:"added"`
}

// KeyStore is a TOFU (Trust On First Use) key store for device identity.
// It maps client names to their registered device public keys.
// All methods are goroutine-safe.
type KeyStore struct {
	mu   sync.RWMutex
	keys map[string]DeviceKey
	path string
}

// NewKeyStore loads a key store from a JSON file, or creates an empty one
// if the file does not exist.
func NewKeyStore(path string) (*KeyStore, error) {
	ks := &KeyStore{
		keys: make(map[string]DeviceKey),
		path: path,
	}

	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return ks, nil
		}
		return nil, fmt.Errorf("read key store: %w", err)
	}

	if err := json.Unmarshal(data, &ks.keys); err != nil {
		return nil, fmt.Errorf("parse key store: %w", err)
	}

	return ks, nil
}

// Check verifies a client's identity public key against the store.
//
// Returns:
//   - ok=true,  isNew=true  — no entry for name (TOFU: caller should register)
//   - ok=true,  isNew=false — entry exists and pubkey matches
//   - ok=false, isNew=false — entry exists but pubkey does NOT match (different device)
func (ks *KeyStore) Check(name string, pubkey string) (ok bool, isNew bool) {
	ks.mu.RLock()
	defer ks.mu.RUnlock()

	existing, found := ks.keys[name]
	if !found {
		return true, true
	}
	if existing.PublicKey == pubkey {
		return true, false
	}
	return false, false
}

// CheckAndRegister atomically checks a client's identity and registers it via
// TOFU if new. This prevents a race where two goroutines both see isNew=true
// and one overwrites the other's registration.
//
// Returns:
//   - ok=true,  isNew=true  — first use, pubkey registered (TOFU)
//   - ok=true,  isNew=false — known device, pubkey matches
//   - ok=false, isNew=false — known device, pubkey mismatch (different device)
func (ks *KeyStore) CheckAndRegister(name, pubkey string) (ok bool, isNew bool, err error) {
	ks.mu.Lock()
	defer ks.mu.Unlock()

	existing, found := ks.keys[name]
	if found {
		if existing.PublicKey == pubkey {
			return true, false, nil
		}
		return false, false, nil
	}

	// TOFU: register new device
	ks.keys[name] = DeviceKey{PublicKey: pubkey, Added: time.Now().UTC()}
	if err := ks.save(); err != nil {
		delete(ks.keys, name) // rollback on save failure
		return false, false, err
	}
	return true, true, nil
}

// Register adds or updates a device key for the given client name.
// The key store is persisted to disk atomically.
func (ks *KeyStore) Register(name, pubkey string) error {
	ks.mu.Lock()
	defer ks.mu.Unlock()

	ks.keys[name] = DeviceKey{
		PublicKey: pubkey,
		Added:     time.Now().UTC(),
	}

	return ks.save()
}

// Remove deletes a device key for the given client name.
// The key store is persisted to disk atomically.
func (ks *KeyStore) Remove(name string) error {
	ks.mu.Lock()
	defer ks.mu.Unlock()

	delete(ks.keys, name)
	return ks.save()
}

// save writes the key store to disk atomically (write temp file, rename).
// Caller must hold ks.mu write lock.
func (ks *KeyStore) save() error {
	data, err := json.MarshalIndent(ks.keys, "", "  ")
	if err != nil {
		return fmt.Errorf("marshal key store: %w", err)
	}

	dir := filepath.Dir(ks.path)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return fmt.Errorf("create key store directory: %w", err)
	}

	tmp := ks.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0600); err != nil {
		return fmt.Errorf("write key store temp: %w", err)
	}

	if err := os.Rename(tmp, ks.path); err != nil {
		os.Remove(tmp) // best-effort cleanup
		return fmt.Errorf("rename key store: %w", err)
	}

	return nil
}
