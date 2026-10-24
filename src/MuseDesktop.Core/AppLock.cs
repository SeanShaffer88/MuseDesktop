using System.Security.Cryptography;

namespace MuseDesktop.Core;

/// <summary>
/// A stored app-lock credential record (S7 #64): a random salt and the
/// PBKDF2-HMAC-SHA256 hash of the credential — never the credential
/// itself, and nothing reversible. The iteration count travels with
/// the record so the cost factor can rise in later versions without
/// invalidating existing locks.
/// </summary>
public sealed record AppLockRecord(string SaltBase64, string HashBase64, int Iterations);

/// <summary>
/// The optional app lock (v0.11.0, S7 #64): a password or PIN the user
/// sets inside MuseDesktop (SEAN's design call — deliberately NOT
/// Windows Hello). The lock gates the app's UI: at startup, on demand
/// from the tray/⋯ menus, and (optionally, default on) when the window
/// hides to the tray. It stops casual access to the OPEN app; it does
/// not encrypt anything on disk — see KNOWN_LIMITATIONS.
///
/// Storage is three settings fields (salt, hash, iterations). The
/// model is fail-open by design: a missing or malformed record means
/// "no lock", never a lockout — <see cref="AppSettings.Sanitized"/>
/// disables an enabled-but-recordless lock, so a hand-edited or
/// deleted settings entry can always recover the app (the documented
/// recovery is deleting settings.json; see the Settings text).
/// </summary>
public static class AppLock
{
    /// <summary>Shortest accepted credential (a 4-digit PIN is the floor; the UI recommends 6+).</summary>
    public const int MinCredentialLength = 4;

    /// <summary>Longest accepted credential.</summary>
    public const int MaxCredentialLength = 64;

    /// <summary>PBKDF2 iterations for newly set credentials (same cost as the #65 encrypted export).</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>Records outside this iteration range are treated as malformed.</summary>
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;

    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>Length gate shared by Set/change flows (the UI checks before asking twice).</summary>
    public static bool IsCredentialLengthValid(string? credential) =>
        credential is not null && credential.Length is >= MinCredentialLength and <= MaxCredentialLength;

    /// <summary>Create a fresh record for a credential (new random salt every time).</summary>
    /// <exception cref="ArgumentException">The credential's length is outside 4–64.</exception>
    public static AppLockRecord CreateRecord(string credential)
    {
        if (!IsCredentialLengthValid(credential))
            throw new ArgumentException(
                $"An app-lock password must be {MinCredentialLength}–{MaxCredentialLength} characters.", nameof(credential));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(credential, salt, DefaultIterations);
        return new AppLockRecord(
            Convert.ToBase64String(salt), Convert.ToBase64String(hash), DefaultIterations);
    }

    /// <summary>
    /// Parse + structurally validate the stored fields. False for
    /// anything missing, undecodable, wrong-sized, or with an
    /// out-of-range iteration count — the caller treats that as
    /// "no lock" (fail-open, never a lockout).
    /// </summary>
    public static bool TryParseRecord(string? saltBase64, string? hashBase64, int iterations, out AppLockRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(saltBase64) || string.IsNullOrWhiteSpace(hashBase64))
            return false;
        if (iterations is < MinIterations or > MaxIterations)
            return false;
        byte[] salt, hash;
        try
        {
            salt = Convert.FromBase64String(saltBase64);
            hash = Convert.FromBase64String(hashBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length != SaltBytes || hash.Length != HashBytes)
            return false;
        record = new AppLockRecord(saltBase64, hashBase64, iterations);
        return true;
    }

    /// <summary>The settings' stored record, when one exists and is well-formed.</summary>
    public static bool TryGetRecord(AppSettings settings, out AppLockRecord? record) =>
        TryParseRecord(settings.AppLockSalt, settings.AppLockHash, settings.AppLockIterations, out record);

    /// <summary>True when a well-formed credential record is stored (regardless of the on/off switch).</summary>
    public static bool HasValidRecord(AppSettings settings) => TryGetRecord(settings, out _);

    /// <summary>
    /// True when the lock should actually engage: switched on AND a
    /// valid record exists. Enabling with no credential set is
    /// impossible by construction — this returns false, and
    /// Sanitized() turns the switch back off.
    /// </summary>
    public static bool IsLockActive(AppSettings settings) =>
        settings.AppLockEnabled && HasValidRecord(settings);

    /// <summary>Check a typed credential against the record (constant-time comparison).</summary>
    public static bool Verify(string? credential, AppLockRecord record)
    {
        if (string.IsNullOrEmpty(credential))
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(record.SaltBase64);
            expected = Convert.FromBase64String(record.HashBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Derive(credential, salt, record.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Set (or change) the credential on a settings object and switch the lock on.</summary>
    public static void SetCredential(AppSettings settings, string credential)
    {
        var record = CreateRecord(credential);
        settings.AppLockSalt = record.SaltBase64;
        settings.AppLockHash = record.HashBase64;
        settings.AppLockIterations = record.Iterations;
        settings.AppLockEnabled = true;
    }

    /// <summary>Remove the credential entirely and switch the lock off. The forgotten-password recovery (deleting settings.json) achieves the same by losing these fields.</summary>
    public static void RemoveCredential(AppSettings settings)
    {
        settings.AppLockSalt = null;
        settings.AppLockHash = null;
        settings.AppLockIterations = 0;
        settings.AppLockEnabled = false;
    }

    private static byte[] Derive(string credential, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(credential, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}

/// <summary>
/// The unlock throttle (S7 #64): after 5 consecutive wrong tries, a
/// 30-second cooldown during which no attempt is accepted. There is
/// deliberately NO lockout-forever and NO data wipe — the credential
/// also gates nothing but this app's UI, and PBKDF2's cost already
/// makes each guess slow. The counter resets on success; failures
/// during a cooldown don't extend it, but a failure right after one
/// expires starts a fresh cooldown (the count is still at the limit).
/// Pure state machine on caller-supplied time, so it's fully testable.
/// </summary>
public sealed class AppLockThrottle
{
    /// <summary>Consecutive failures that trigger a cooldown.</summary>
    public const int FailuresBeforeCooldown = 5;

    /// <summary>How long a cooldown lasts.</summary>
    public static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);

    /// <summary>Wrong tries since the last success.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>When the current cooldown ends (null when not cooling down).</summary>
    public DateTimeOffset? CooldownEndsUtc { get; private set; }

    /// <summary>True while attempts are refused.</summary>
    public bool IsCoolingDown(DateTimeOffset nowUtc) =>
        CooldownEndsUtc is { } end && nowUtc < end;

    /// <summary>Time left in the cooldown (zero when not cooling down).</summary>
    public TimeSpan RemainingCooldown(DateTimeOffset nowUtc) =>
        IsCoolingDown(nowUtc) ? CooldownEndsUtc!.Value - nowUtc : TimeSpan.Zero;

    /// <summary>
    /// Record one wrong try. Returns true when this failure STARTED a
    /// cooldown. A failure during a cooldown is not counted (the UI
    /// disables the button anyway) and never extends it.
    /// </summary>
    public bool RecordFailure(DateTimeOffset nowUtc)
    {
        if (IsCoolingDown(nowUtc))
            return false;
        ConsecutiveFailures++;
        if (ConsecutiveFailures >= FailuresBeforeCooldown)
        {
            CooldownEndsUtc = nowUtc + CooldownDuration;
            return true;
        }
        return false;
    }

    /// <summary>A successful unlock clears the counter and any cooldown.</summary>
    public void RecordSuccess()
    {
        ConsecutiveFailures = 0;
        CooldownEndsUtc = null;
    }
}
