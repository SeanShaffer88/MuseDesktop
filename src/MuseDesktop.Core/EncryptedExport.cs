using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>Why an encrypted-export read failed (S7 #65) — the two user-actionable cases are kept distinct.</summary>
public enum EncryptedExportError
{
    None,

    /// <summary>The text isn't an encrypted export at all (no magic header).</summary>
    NotEncrypted,

    /// <summary>The envelope is damaged: bad structure, bad base64, unknown version.</summary>
    Damaged,

    /// <summary>
    /// The envelope parsed but AES-GCM authentication failed — the
    /// password is wrong, or the ciphertext was altered after export.
    /// (GCM cannot distinguish those two; the message says both.)
    /// </summary>
    WrongPasswordOrAltered,
}

/// <summary>
/// Password-encrypted JSON export (S7 #65), built only on .NET's
/// standard cryptography — no new dependencies. It carries settings JSON
/// or a versioned local-data backup; the original magic line is retained
/// so earlier settings exports remain recognizable. Format:
///
///   line 1:  MUSEDESKTOP-SETTINGS-ENCRYPTED
///   rest:    a JSON envelope { version, iterations, salt, nonce, tag,
///            data } with the binary fields base64'd.
///
/// The key is PBKDF2-HMAC-SHA256 (210,000 iterations, 16-byte random
/// salt) from the user's password; the payload is the ordinary
/// settings JSON encrypted with AES-256-GCM (12-byte random nonce).
/// The plain export stays available; this exists for users who keep
/// exports somewhere shared and don't want profile names and start
/// pages readable in clear text.
/// </summary>
public static class EncryptedExport
{
    public const string Magic = "MUSEDESKTOP-SETTINGS-ENCRYPTED";
    public const int FormatVersion = 1;
    public const int Iterations = 210_000;
    public const int MaxPlainBytes = 32 * 1024 * 1024;
    public const int MaxCipherChars = ((MaxPlainBytes + 2) / 3) * 4;
    // A legacy JSON writer may encode each Base64 character as \uXXXX.
    // Budget for that representation, while bounding decoded fields below.
    public const int MaxEnvelopeChars = MaxCipherChars * 6 + 4096;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>True when <paramref name="text"/> carries the encrypted-export magic (first non-blank line).</summary>
    public static bool IsEncrypted(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var firstLine = text.TrimStart().Split('\n', 2)[0].Trim();
        return firstLine == Magic;
    }

    /// <summary>Encrypt settings JSON with <paramref name="password"/> (must be non-empty) and return the file text.</summary>
    public static string Encrypt(string plainJson, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is required to encrypt an export.", nameof(password));
        if (Encoding.UTF8.GetByteCount(plainJson) > MaxPlainBytes)
            throw new ArgumentException("Export exceeds the 32 MB encrypted backup limit.", nameof(plainJson));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);
        byte[]? key = null;
        try
        {
            key = DeriveKey(password, salt, Iterations);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(key, TagBytes))
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
            var envelope = new Envelope
            {
                Version = FormatVersion, Iterations = Iterations,
                Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipherBytes),
            };
            return Magic + "\n" + JsonSerializer.Serialize(envelope, EnvelopeOptions) + "\n";
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// Decrypt an encrypted export. On failure <paramref name="error"/>
    /// distinguishes a damaged envelope from a wrong password, and
    /// <paramref name="message"/> says it in plain words.
    /// </summary>
    public static bool TryDecrypt(string text, string password,
        out string? plainJson, out EncryptedExportError error, out string? message)
    {
        plainJson = null;
        error = EncryptedExportError.None;
        message = null;
        if (text is { Length: > MaxEnvelopeChars })
        {
            error = EncryptedExportError.Damaged;
            message = "That encrypted export exceeds the 32 MB backup limit.";
            return false;
        }
        if (!IsEncrypted(text))
        {
            error = EncryptedExportError.NotEncrypted;
            message = "That file isn't an encrypted MuseDesktop export.";
            return false;
        }
        Envelope? envelope;
        try
        {
            var body = text.TrimStart()[(text.TrimStart().IndexOf('\n') + 1)..];
            envelope = JsonSerializer.Deserialize<Envelope>(body, EnvelopeOptions);
        }
        catch (Exception)
        {
            envelope = null;
        }
        if (envelope is null || envelope.Version != FormatVersion
            || envelope.Iterations is < 100_000 or > 10_000_000)
        {
            error = EncryptedExportError.Damaged;
            message = "That encrypted export is damaged or from an unsupported version — it can't be opened.";
            return false;
        }
        byte[] salt, nonce, tag, data;
        try
        {
            // Check before Base64 allocates: tiny IV/tag fields must not be
            // used to allocate payload-sized buffers, even in hostile JSON.
            if ((envelope.Salt?.Length ?? 0) > 64 || (envelope.Nonce?.Length ?? 0) > 64 ||
                (envelope.Tag?.Length ?? 0) > 64 || (envelope.Data?.Length ?? 0) > MaxCipherChars)
                throw new FormatException("field size");
            salt = Convert.FromBase64String(envelope.Salt ?? "");
            nonce = Convert.FromBase64String(envelope.Nonce ?? "");
            tag = Convert.FromBase64String(envelope.Tag ?? "");
            data = Convert.FromBase64String(envelope.Data ?? "");
            if (salt.Length != SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes ||
                data.Length == 0 || data.Length > MaxPlainBytes)
                throw new FormatException("field size");
        }
        catch (Exception)
        {
            error = EncryptedExportError.Damaged;
            message = "That encrypted export is damaged or from an unsupported version — it can't be opened.";
            return false;
        }
        byte[]? key = null;
        byte[]? plainBytes = null;
        try
        {
            key = DeriveKey(password ?? "", salt, envelope.Iterations);
            plainBytes = new byte[data.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, data, tag, plainBytes);
            plainJson = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (CryptographicException)
        {
            error = EncryptedExportError.WrongPasswordOrAltered;
            message = "That password didn't open the file — it's wrong, or the file was altered after export.";
            return false;
        }
        catch (Exception)
        {
            error = EncryptedExportError.Damaged;
            message = "That encrypted export is damaged or from an unsupported version — it can't be opened.";
            return false;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class Envelope
    {
        public int Version { get; set; }
        public int Iterations { get; set; }
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? Data { get; set; }
    }
}
