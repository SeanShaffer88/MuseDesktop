using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// S7 #64: the app lock — salted PBKDF2 record round-trip, wrong
/// credential, sanitize of corrupt records (fail-open, never a
/// lockout), the unlock throttle state machine, the settings
/// round-trip of the new fields, and "can't enable with no
/// credential set".
/// </summary>
public class AppLockTests
{
    [Fact]
    public void Record_VerifiesTheRightCredentialOnly()
    {
        var record = AppLock.CreateRecord("correct horse 7");
        Assert.True(AppLock.Verify("correct horse 7", record));
        Assert.False(AppLock.Verify("correct horse 8", record));
        Assert.False(AppLock.Verify("", record));
        Assert.False(AppLock.Verify(null, record));
    }

    [Fact]
    public void Record_AcceptsAPinAndRejectsBadLengths()
    {
        var pin = AppLock.CreateRecord("1234");
        Assert.True(AppLock.Verify("1234", pin));
        Assert.False(AppLock.IsCredentialLengthValid("123"));
        Assert.False(AppLock.IsCredentialLengthValid(new string('x', 65)));
        Assert.False(AppLock.IsCredentialLengthValid(null));
        Assert.Throws<ArgumentException>(() => AppLock.CreateRecord("123"));
        Assert.Throws<ArgumentException>(() => AppLock.CreateRecord(new string('x', 65)));
    }

    [Fact]
    public void Record_FreshSaltEveryTime()
    {
        var a = AppLock.CreateRecord("same password");
        var b = AppLock.CreateRecord("same password");
        Assert.NotEqual(a.SaltBase64, b.SaltBase64);
        Assert.NotEqual(a.HashBase64, b.HashBase64);
        Assert.True(AppLock.Verify("same password", a));
        Assert.True(AppLock.Verify("same password", b));
        Assert.Equal(AppLock.DefaultIterations, a.Iterations);
        Assert.True(a.Iterations >= 100_000);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("", "", 210_000)]
    [InlineData("not base64!!", "also not", 210_000)]
    [InlineData("AAAA", "AAAA", 210_000)] // decodes, but wrong sizes
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", 5)] // right sizes, iterations too low
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", 99_999_999)] // iterations too high
    public void TryParseRecord_RejectsMalformed(string? salt, string? hash, int iterations)
    {
        Assert.False(AppLock.TryParseRecord(salt, hash, iterations, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void SetAndRemoveCredential_OnSettings()
    {
        var s = new AppSettings();
        Assert.False(AppLock.IsLockActive(s));

        AppLock.SetCredential(s, "hunter2-plus");
        Assert.True(s.AppLockEnabled);
        Assert.True(AppLock.HasValidRecord(s));
        Assert.True(AppLock.IsLockActive(s));
        Assert.True(AppLock.TryGetRecord(s, out var record));
        Assert.True(AppLock.Verify("hunter2-plus", record!));
        // The credential itself is nowhere in the stored fields.
        Assert.DoesNotContain("hunter2", s.AppLockHash);
        Assert.DoesNotContain("hunter2", s.AppLockSalt);

        AppLock.RemoveCredential(s);
        Assert.False(s.AppLockEnabled);
        Assert.Null(s.AppLockSalt);
        Assert.Null(s.AppLockHash);
        Assert.Equal(0, s.AppLockIterations);
        Assert.False(AppLock.IsLockActive(s));
    }

    [Fact]
    public void EnablingWithoutACredential_IsImpossible()
    {
        // The switch alone does nothing...
        var s = new AppSettings { AppLockEnabled = true };
        Assert.False(AppLock.IsLockActive(s));
        // ...and Sanitized turns it back off, flagging the repair for the log.
        s.Sanitized();
        Assert.False(s.AppLockEnabled);
        Assert.True(s.AppLockWasRepaired);
    }

    [Fact]
    public void Sanitized_RepairsACorruptRecord_AndLeavesAGoodOneAlone()
    {
        var corrupt = new AppSettings
        {
            AppLockEnabled = true,
            AppLockSalt = "garbage!!",
            AppLockHash = "more garbage",
            AppLockIterations = 210_000,
        };
        corrupt.Sanitized();
        Assert.False(corrupt.AppLockEnabled);
        Assert.Null(corrupt.AppLockSalt);
        Assert.Null(corrupt.AppLockHash);
        Assert.True(corrupt.AppLockWasRepaired);

        var good = new AppSettings();
        AppLock.SetCredential(good, "still here 9");
        good.Sanitized();
        Assert.True(good.AppLockEnabled);
        Assert.True(AppLock.IsLockActive(good));
        Assert.False(good.AppLockWasRepaired);
    }

    [Fact]
    public void LockFields_SurviveCloneAndExportImport()
    {
        var s = new AppSettings { AppLockOnTrayHide = false };
        AppLock.SetCredential(s, "round trip 42");

        var clone = s.Clone();
        Assert.True(clone.AppLockEnabled);
        Assert.Equal(s.AppLockSalt, clone.AppLockSalt);
        Assert.Equal(s.AppLockHash, clone.AppLockHash);
        Assert.Equal(s.AppLockIterations, clone.AppLockIterations);
        Assert.False(clone.AppLockOnTrayHide);
        Assert.True(AppLock.IsLockActive(clone));

        var json = SettingsStore.Export(s);
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error), error);
        Assert.True(AppLock.IsLockActive(imported!));
        Assert.True(AppLock.TryGetRecord(imported!, out var record));
        Assert.True(AppLock.Verify("round trip 42", record!));
        // The repair flag is diagnostic-only and never persisted.
        Assert.DoesNotContain("appLockWasRepaired", json);
    }

    [Fact]
    public void CorruptLockRecordInSettingsFile_LoadsUnlocked()
    {
        // The forgotten-password / damaged-file recovery path: whatever
        // is wrong with the record, Load must return usable settings
        // with the lock off — never a bricked app.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "musedesktop-applock-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path,
                "{\"appLockEnabled\":true,\"appLockSalt\":\"@@bad@@\",\"appLockHash\":\"%%bad%%\",\"appLockIterations\":210000}");
            var loaded = SettingsStore.Load(path);
            Assert.False(loaded.AppLockEnabled);
            Assert.False(AppLock.IsLockActive(loaded));
            Assert.True(loaded.AppLockWasRepaired);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Throttle_FiveFailuresStartACooldown()
    {
        var throttle = new AppLockThrottle();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i < AppLockThrottle.FailuresBeforeCooldown; i++)
        {
            Assert.False(throttle.RecordFailure(now));
            Assert.False(throttle.IsCoolingDown(now));
            Assert.Equal(i, throttle.ConsecutiveFailures);
        }
        Assert.True(throttle.RecordFailure(now)); // the 5th starts it
        Assert.True(throttle.IsCoolingDown(now));
        Assert.True(throttle.RemainingCooldown(now) > TimeSpan.FromSeconds(29));
        Assert.True(throttle.RemainingCooldown(now) <= AppLockThrottle.CooldownDuration);
    }

    [Fact]
    public void Throttle_CooldownIsNotExtendedByMoreFailures_AndSuccessResets()
    {
        var throttle = new AppLockThrottle();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < AppLockThrottle.FailuresBeforeCooldown; i++)
            throttle.RecordFailure(now);
        var ends = throttle.CooldownEndsUtc;

        // Failures during the cooldown: not counted, no extension.
        Assert.False(throttle.RecordFailure(now.AddSeconds(5)));
        Assert.Equal(ends, throttle.CooldownEndsUtc);
        Assert.Equal(AppLockThrottle.FailuresBeforeCooldown, throttle.ConsecutiveFailures);

        // After it lapses, one more failure re-cools immediately...
        var later = now.AddSeconds(31);
        Assert.False(throttle.IsCoolingDown(later));
        Assert.True(throttle.RecordFailure(later));
        Assert.True(throttle.IsCoolingDown(later));

        // ...and a success clears everything.
        throttle.RecordSuccess();
        Assert.Equal(0, throttle.ConsecutiveFailures);
        Assert.Null(throttle.CooldownEndsUtc);
        Assert.False(throttle.IsCoolingDown(later));
    }
}
