package identity

import (
	"encoding/hex"
	"os"
	"path/filepath"
	"testing"

	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
)

func TestLoadOrGenerateKey(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "identity.key")

	// Generate new key.
	key1, err := LoadOrGenerateKey(path)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	if key1 == nil {
		t.Fatal("key is nil")
	}

	// File must exist with 0600 permissions.
	info, err := os.Stat(path)
	if err != nil {
		t.Fatalf("stat key file: %v", err)
	}
	if perm := info.Mode().Perm(); perm != 0600 {
		t.Errorf("key file permissions = %o, want 0600", perm)
	}

	// Load same key back.
	key2, err := LoadOrGenerateKey(path)
	if err != nil {
		t.Fatalf("load key: %v", err)
	}

	if !key1.PublicKey().Equal(key2.PublicKey()) {
		t.Error("loaded key does not match generated key")
	}
}

func TestLoadOrGenerateKey_CreatesParentDirs(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "deep", "nested", "identity.key")

	key, err := LoadOrGenerateKey(path)
	if err != nil {
		t.Fatalf("generate key with nested dirs: %v", err)
	}
	if key == nil {
		t.Fatal("key is nil")
	}

	// Verify file exists.
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("key file not created: %v", err)
	}
}

func TestLoadOrGenerateKey_ExistingFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "identity.key")

	// Generate a known key and write hex to file.
	orig, err := btcrypto.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate keypair: %v", err)
	}
	encoded := hex.EncodeToString(orig.Bytes())
	if err := os.WriteFile(path, []byte(encoded), 0600); err != nil {
		t.Fatalf("write key file: %v", err)
	}

	// Load it.
	loaded, err := LoadOrGenerateKey(path)
	if err != nil {
		t.Fatalf("load existing key: %v", err)
	}

	if !orig.PublicKey().Equal(loaded.PublicKey()) {
		t.Error("loaded key does not match written key")
	}
}

func TestLoadOrGenerateKey_TrailingNewline(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "identity.key")

	// Generate a key and write with trailing newline (common after copy-paste).
	orig, err := btcrypto.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate keypair: %v", err)
	}
	encoded := hex.EncodeToString(orig.Bytes()) + "\n"
	if err := os.WriteFile(path, []byte(encoded), 0600); err != nil {
		t.Fatalf("write key file: %v", err)
	}

	loaded, err := LoadOrGenerateKey(path)
	if err != nil {
		t.Fatalf("load key with trailing newline: %v", err)
	}
	if !orig.PublicKey().Equal(loaded.PublicKey()) {
		t.Error("loaded key does not match original")
	}
}

func TestLoadOrGenerateKey_InvalidHex(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "identity.key")

	if err := os.WriteFile(path, []byte("not-hex-data!"), 0600); err != nil {
		t.Fatalf("write file: %v", err)
	}

	_, err := LoadOrGenerateKey(path)
	if err == nil {
		t.Error("expected error for invalid hex, got nil")
	}
}

func TestComputeAndVerifyProof(t *testing.T) {
	// Client side: identity key.
	identityKey, err := btcrypto.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate identity key: %v", err)
	}

	// Exit side: DH key.
	exitKey, err := btcrypto.GenerateKeyPair()
	if err != nil {
		t.Fatalf("generate exit key: %v", err)
	}

	name := "test-device"

	// Client computes proof.
	proof, err := ComputeIdentityProof(identityKey, exitKey.PublicKey(), name)
	if err != nil {
		t.Fatalf("compute proof: %v", err)
	}

	if len(proof) != 32 { // SHA-256 output
		t.Errorf("proof length = %d, want 32", len(proof))
	}

	// Exit verifies proof.
	if !VerifyIdentityProof(exitKey, identityKey.PublicKey(), name, proof) {
		t.Error("valid proof was rejected")
	}
}

func TestVerifyProof_WrongKey(t *testing.T) {
	identityKey, _ := btcrypto.GenerateKeyPair()
	wrongKey, _ := btcrypto.GenerateKeyPair()
	exitKey, _ := btcrypto.GenerateKeyPair()

	name := "test-device"

	// Compute proof with the real identity key.
	proof, err := ComputeIdentityProof(identityKey, exitKey.PublicKey(), name)
	if err != nil {
		t.Fatalf("compute proof: %v", err)
	}

	// Verify with the WRONG identity public key.
	if VerifyIdentityProof(exitKey, wrongKey.PublicKey(), name, proof) {
		t.Error("proof with wrong identity key should have been rejected")
	}
}

func TestVerifyProof_WrongName(t *testing.T) {
	identityKey, _ := btcrypto.GenerateKeyPair()
	exitKey, _ := btcrypto.GenerateKeyPair()

	// Compute proof with name "alice".
	proof, err := ComputeIdentityProof(identityKey, exitKey.PublicKey(), "alice")
	if err != nil {
		t.Fatalf("compute proof: %v", err)
	}

	// Verify with name "bob" — must fail.
	if VerifyIdentityProof(exitKey, identityKey.PublicKey(), "bob", proof) {
		t.Error("proof with wrong name should have been rejected")
	}
}

func TestVerifyProof_TamperedProof(t *testing.T) {
	identityKey, _ := btcrypto.GenerateKeyPair()
	exitKey, _ := btcrypto.GenerateKeyPair()

	proof, err := ComputeIdentityProof(identityKey, exitKey.PublicKey(), "device")
	if err != nil {
		t.Fatalf("compute proof: %v", err)
	}

	// Flip a bit.
	tampered := make([]byte, len(proof))
	copy(tampered, proof)
	tampered[0] ^= 0x01

	if VerifyIdentityProof(exitKey, identityKey.PublicKey(), "device", tampered) {
		t.Error("tampered proof should have been rejected")
	}
}

func TestKeyStore_TOFU(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	ks, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}

	// First check: unknown name → TOFU.
	ok, isNew := ks.Check("mac", "pubkey-abc")
	if !ok || !isNew {
		t.Errorf("TOFU check: ok=%v isNew=%v, want ok=true isNew=true", ok, isNew)
	}

	// Register the key.
	if err := ks.Register("mac", "pubkey-abc"); err != nil {
		t.Fatalf("register: %v", err)
	}

	// Second check: known name, same key → ok.
	ok, isNew = ks.Check("mac", "pubkey-abc")
	if !ok || isNew {
		t.Errorf("known key check: ok=%v isNew=%v, want ok=true isNew=false", ok, isNew)
	}
}

func TestKeyStore_Reject(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	ks, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}

	// Register key A for "mac".
	if err := ks.Register("mac", "pubkey-A"); err != nil {
		t.Fatalf("register: %v", err)
	}

	// Check with key B → rejected.
	ok, isNew := ks.Check("mac", "pubkey-B")
	if ok || isNew {
		t.Errorf("different key check: ok=%v isNew=%v, want ok=false isNew=false", ok, isNew)
	}
}

func TestKeyStore_Persistence(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	// Create and register.
	ks1, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}
	if err := ks1.Register("phone", "pubkey-phone"); err != nil {
		t.Fatalf("register: %v", err)
	}

	// Load from same file.
	ks2, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("reload key store: %v", err)
	}

	ok, isNew := ks2.Check("phone", "pubkey-phone")
	if !ok || isNew {
		t.Errorf("persisted check: ok=%v isNew=%v, want ok=true isNew=false", ok, isNew)
	}

	// Different key still rejected after reload.
	ok, isNew = ks2.Check("phone", "pubkey-other")
	if ok || isNew {
		t.Errorf("persisted reject: ok=%v isNew=%v, want ok=false isNew=false", ok, isNew)
	}
}

func TestKeyStore_Remove(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	ks, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}

	// Register then remove.
	if err := ks.Register("laptop", "pubkey-laptop"); err != nil {
		t.Fatalf("register: %v", err)
	}
	if err := ks.Remove("laptop"); err != nil {
		t.Fatalf("remove: %v", err)
	}

	// Check after remove: should be TOFU again.
	ok, isNew := ks.Check("laptop", "pubkey-laptop")
	if !ok || !isNew {
		t.Errorf("after remove: ok=%v isNew=%v, want ok=true isNew=true", ok, isNew)
	}
}

func TestKeyStore_Remove_Persistence(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	ks1, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}
	if err := ks1.Register("laptop", "pubkey-laptop"); err != nil {
		t.Fatalf("register: %v", err)
	}
	if err := ks1.Remove("laptop"); err != nil {
		t.Fatalf("remove: %v", err)
	}

	// Reload and verify removal persisted.
	ks2, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("reload key store: %v", err)
	}
	ok, isNew := ks2.Check("laptop", "pubkey-laptop")
	if !ok || !isNew {
		t.Errorf("persisted remove: ok=%v isNew=%v, want ok=true isNew=true", ok, isNew)
	}
}

func TestKeyStore_CheckAndRegister_Atomic(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "keys.json")

	ks, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store: %v", err)
	}

	// First call: TOFU registers
	ok, isNew, err := ks.CheckAndRegister("mac", "pubkey-A")
	if err != nil {
		t.Fatalf("check and register: %v", err)
	}
	if !ok || !isNew {
		t.Errorf("first CheckAndRegister: ok=%v isNew=%v, want ok=true isNew=true", ok, isNew)
	}

	// Same key again: known device
	ok, isNew, err = ks.CheckAndRegister("mac", "pubkey-A")
	if err != nil {
		t.Fatalf("check and register same: %v", err)
	}
	if !ok || isNew {
		t.Errorf("same key CheckAndRegister: ok=%v isNew=%v, want ok=true isNew=false", ok, isNew)
	}

	// Different key: rejected
	ok, isNew, err = ks.CheckAndRegister("mac", "pubkey-B")
	if err != nil {
		t.Fatalf("check and register diff: %v", err)
	}
	if ok || isNew {
		t.Errorf("diff key CheckAndRegister: ok=%v isNew=%v, want ok=false isNew=false", ok, isNew)
	}

	// Verify persistence
	ks2, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("reload: %v", err)
	}
	ok, isNew = ks2.Check("mac", "pubkey-A")
	if !ok || isNew {
		t.Errorf("persisted: ok=%v isNew=%v, want ok=true isNew=false", ok, isNew)
	}
}

func TestKeyStore_NonexistentFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "nonexistent", "keys.json")

	ks, err := NewKeyStore(path)
	if err != nil {
		t.Fatalf("new key store from nonexistent: %v", err)
	}

	// Should work as empty store.
	ok, isNew := ks.Check("any", "any-key")
	if !ok || !isNew {
		t.Errorf("empty store check: ok=%v isNew=%v, want ok=true isNew=true", ok, isNew)
	}

	// Register should create the file (and parent dirs).
	if err := ks.Register("any", "any-key"); err != nil {
		t.Fatalf("register on nonexistent path: %v", err)
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("key store file not created: %v", err)
	}
}
