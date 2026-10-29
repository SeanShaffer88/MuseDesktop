using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task ConcurrentWritesLeaveOneCompletePayloadAndNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"musedesktop-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.json");
        var payloads = Enumerable.Range(0, 8)
            .Select(i => $"payload-{i}:" + new string((char)('a' + i), 20_000))
            .ToArray();

        try
        {
            await Task.WhenAll(payloads.Select(payload => Task.Run(() =>
            {
                for (var i = 0; i < 5; i++)
                    AtomicFile.WriteAllText(path, payload);
            })));

            Assert.Contains(File.ReadAllText(path), payloads);
            Assert.Empty(Directory.GetFiles(directory, "data.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AsyncWriteReplacesTargetAndCancelledWriteKeepsIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"musedesktop-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "async.json");
        File.WriteAllText(path, "old");

        try
        {
            await AtomicFile.WriteAllTextAsync(path, "new payload");
            Assert.Equal("new payload", File.ReadAllText(path));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFile.WriteAllTextAsync(path, "must not replace", cancelled.Token));
            Assert.Equal("new payload", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(directory, "async.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedReplacePreservesTargetAndCleansTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"musedesktop-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "target.json");
        Directory.CreateDirectory(target);

        try
        {
            var error = Record.Exception(() => AtomicFile.WriteAllText(target, "replacement"));
            // Replacing a directory is an I/O failure on Unix and access-denied on Windows.
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.GetFiles(directory, "target.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
