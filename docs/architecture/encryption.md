# Encryption Architecture

## Overview

ReStore encrypts with AES-256-GCM throughout, but the two artifact types derive their keys
differently because they have different requirements. System archives are opaque blobs, so each
gets a random Data Encryption Key wrapped by a password-derived Key Encryption Key. Chunk
objects must stay deduplicable, so their keys are derived deterministically from the chunk's own
content hash.

Both paths start from the same master key: PBKDF2-SHA256 over your password and the salt in
`config.json`, at 1,000,000 iterations.

## System Archives: DEK + KEK

Used for system backups (programs, environment variables, Windows settings).

```
Master Password (User Input)
      │
      ↓ PBKDF2-SHA256 (1M iterations)
Master Salt (32 bytes) ────────→ KEK (Key Encryption Key, 32 bytes)
      │                              │
      │                              ↓
      │                         Encrypts DEK
      │                              │
      ↓                              ↓
Verification Token          Encrypted DEK (stored in .enc.meta)
(stored in config)                   │
                                     ↓
                              DEK (Data Encryption Key, 32 bytes)
                                     │
                                     ↓ AES-256-GCM
                              Encrypts Archive Content
                                     │
                                     ↓
                              .enc file (encrypted backup)
```

## Chunk Objects: Deterministic Per-Chunk Keys

Used for user-file backups. A random key per chunk would encrypt identical plaintext to
different ciphertext, which destroys deduplication — the whole point of the chunk store. So the
key and IV come from the master key and the chunk's plaintext hash instead:

```
Master Key (from PBKDF2)  +  Chunk Content Hash
      │
      ↓ HMAC-SHA256, labelled "chunk-key" / "chunk-iv"
Per-Chunk Key (32 bytes) + Per-Chunk IV (12 bytes)
      │
      ↓ AES-256-GCM, chunk id as associated data
Encrypted chunk payload (tag prepended)
```

Identical plaintext therefore always produces an identical chunk object, and dedup keeps
working across files and snapshots. The trade-off is that an observer can tell that two chunks
hold the same content — acceptable here, since the chunk id is already its content hash.

Chunks encrypted under different passwords are kept apart by the manifest's
`chunkStorageNamespace`, which is derived from the master key and inserted into the chunk path.

## Key Components

| Component          | Size         | Description                                      |
| ------------------ | ------------ | ------------------------------------------------ |
| Master Password    | User-defined | User-provided password (minimum 8 characters)    |
| Master Salt        | 32 bytes     | Random salt stored in `config.json`              |
| Verification Token | Variable     | Encrypted constant for password validation       |
| KEK                | 32 bytes     | Derived from password + salt using PBKDF2-SHA256 |
| DEK                | 32 bytes     | Random key generated per system archive          |
| Encrypted DEK      | Variable     | DEK encrypted with KEK, stored in `.enc.meta`    |
| Per-chunk key/IV   | 32 / 12 bytes | Derived from master key + chunk content hash    |
| IV                 | 12 bytes     | Random per system archive; derived per chunk     |
| Tag                | 16 bytes     | Authentication tag for AES-GCM                   |

## Security Properties

| Property                 | Implementation                                        |
| ------------------------ | ----------------------------------------------------- |
| Password Protection      | KEK derived from password, never stored               |
| Authenticated Encryption | AES-GCM provides encryption + integrity               |
| Key Derivation           | PBKDF2-SHA256 with 1,000,000 iterations               |
| Per-archive key          | Each system archive gets its own random DEK           |
| Dedup-safe chunks        | Deterministic per-chunk keys keep identical chunks identical |
| Key isolation            | Chunks from different passwords use separate namespaces |
| Password Verification    | Token in `config.json` validates the password locally |
| Tamper detection         | A wrong key or altered payload fails the GCM tag check |

## Critical Files

| File                  | Contents                                          | Storage Location         |
| --------------------- | ------------------------------------------------- | ------------------------ |
| `config.json`         | `encryption.salt`, `encryption.verificationToken` | `%USERPROFILE%\ReStore\` |
| `*.chunk`             | Encrypted deterministic chunk payloads            | Remote storage           |
| `*.manifest.json`     | Snapshot metadata and content-address references  | Remote storage           |
| `backup.zip.enc`      | Encrypted system backup archive data              | Remote storage           |
| `backup.zip.enc.meta` | Encryption metadata (salt, IV, encryptedDEK)      | Remote storage           |

## Metadata Structure

The `.enc.meta` file contains JSON with the following structure:

```json
{
  "Salt": "<base64>",
  "IV": "<base64>",
  "EncryptedDEK": "<base64>",
  "Algorithm": "AES-256-GCM",
  "Version": 1,
  "KeyDerivationIterations": 1000000
}
```

## Recovery Requirements

To decrypt a backup, you need:

1. **Password** - User must remember this
2. **`.enc.meta` file** - Contains salt, IV, and encrypted DEK for system archives
3. **`.enc` file** - The encrypted system backup data
4. **Snapshot manifest** - Required for user-file restores and verification
5. **Chunk objects** - Required for user-file restores and verification

### Recovery Scenarios

| Scenario                   | Outcome                                            |
| -------------------------- | -------------------------------------------------- |
| Lost master salt in config | Can still decrypt old backups — the salt is also stored in each `.enc.meta` and each snapshot manifest |
| Lost `.enc.meta` file      | System archive backup permanently lost             |
| Lost chunk object          | User-file snapshot file(s) cannot be reconstructed |
| Lost password              | All encrypted backups permanently lost             |

## Implementation Details

### EncryptionService Methods

```csharp
// Key derivation
byte[] DeriveKeyFromPassword(string password, byte[] salt, int iterations = 1_000_000)

// System archive encryption (returns metadata for the .enc.meta file)
Task<EncryptionMetadata> EncryptFileAsync(string inputPath, string outputPath,
    string password, byte[]? salt = null, int iterations = 1_000_000)

// File decryption (requires metadata)
Task DecryptFileAsync(string inputPath, string outputPath,
    string password, EncryptionMetadata metadata)

// Password verification token
string CreatePasswordVerificationToken(string password, byte[] salt, int iterations)
bool VerifyPassword(string password, byte[] salt, string verificationToken, int iterations)

// Chunk payloads (static; deterministic per chunk id)
static byte[] EncryptChunkDeterministic(byte[] plaintext, byte[] masterKey, string chunkId)
static byte[] DecryptChunkDeterministic(byte[] payload, byte[] masterKey, string chunkId)
```

### Password Provider Interface

```csharp
public interface IPasswordProvider
{
    Task<string?> GetPasswordAsync();
    bool IsPasswordSet();
    void ClearPassword();
}
```

| Implementation           | Usage                                                        |
| ------------------------ | ------------------------------------------------------------ |
| `StaticPasswordProvider` | Supply a known password programmatically; used by tests      |
| `GuiPasswordProvider`    | WPF dialog prompts with per-session caching                  |
| CLI provider             | Reads `RESTORE_ENCRYPTION_PASSWORD`, otherwise prompts on the console |

`GuiPasswordProvider` additionally exposes `SetEncryptionMode(bool)`. In encryption mode it
validates what the user typed against `encryption.verificationToken` and re-prompts on a
mismatch (three attempts), which is only meaningful when creating a backup. For restore and
verify the mode stays `false`, because a wrong password shows up as a GCM tag failure on the
first chunk anyway.

## Best Practices

1. **Always upload both files**: `.enc` and `.enc.meta` must be uploaded together
2. **Context awareness**: Call `SetEncryptionMode(true)` before backup, keep default `false` for restore
3. **Password caching**: Password is cached for the session in `App.GlobalPasswordProvider`
4. **Error handling**: Clear cached password on decryption failure to allow retry
5. **Validate on backup only**: Password is validated against token during encryption, not decryption
6. **Use synchronous reads**: Use `ReadExactly()` not `ReadExactlyAsync()` in `Task.Run()` blocks
