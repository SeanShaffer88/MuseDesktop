using System.Text;

namespace MuseDesktop.Core;

/// <summary>
/// Writes a complete text file through a unique same-directory temporary
/// file, then replaces the target. Bounded path-hashed lock stripes prevent
/// concurrent in-process writers from interleaving replacements without
/// retaining every path ever written. A failed write leaves the previous
/// target intact and the temporary file is cleaned up when possible.
/// </summary>
public static class AtomicFile
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly SemaphoreSlim[] Gates = CreateGates();

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[64];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var gate = GetGate(fullPath);
        gate.Wait();
        try
        {
            WriteUnderGate(fullPath, contents);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Async counterpart for larger text persistence; shares the same lock stripes and atomic replacement behavior.</summary>
    public static async Task WriteAllTextAsync(
        string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var gate = GetGate(fullPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 bufferSize: 64 * 1024, FileOptions.Asynchronous))
                {
                    await using (var writer = new StreamWriter(
                                     stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                                     bufferSize: 64 * 1024, leaveOpen: true))
                    {
                        await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim GetGate(string fullPath)
    {
        var hash = PathComparer.GetHashCode(fullPath) & int.MaxValue;
        return Gates[hash % Gates.Length];
    }

    private static void WriteUnderGate(string fullPath, string contents)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        catch
        {
            // Preserve the write/replace exception; an orphaned uniquely
            // named temporary file cannot corrupt the destination.
        }
    }
}
