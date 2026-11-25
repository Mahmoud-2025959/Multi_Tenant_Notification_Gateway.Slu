namespace NotificationGateway.Application.Abstractions;

/// <summary>
/// Encrypts provider credentials at rest (AES-256-GCM).
///
/// <paramref name="binding"/> is used as Additional Authenticated Data. We pass "provider-config:{configId}" so an
/// envelope copied into ANOTHER row (e.g. another tenant's configuration) fails authentication on decrypt.
///
/// Envelope format: "v1:{keyId}:{base64(nonce[12] | ciphertext | tag[16])}".
/// The keyId lets several master keys co-exist during key rotation: new writes use the active key, old envelopes are
/// still readable, and a re-encryption job moves them to the active key.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext, string binding);
    string Unprotect(string envelope, string binding);

    /// <summary>True when the envelope was produced with a key that is no longer the active key.</summary>
    bool NeedsReEncryption(string envelope);
}
