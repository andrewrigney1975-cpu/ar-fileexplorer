namespace FileExplorer.Services;

/// Streams a single file with a large buffer, automatic retry, and resume-from-offset on transient failure.
public static class ResilientFileCopy
{
    private const int BufferSize = 1024 * 1024; // 1 MiB
    private const int MaxAttempts = 5;

    public static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        var attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            var resumeOffset = GetResumeOffset(destinationPath, sourceLength);

            try
            {
                await StreamCopyAsync(sourcePath, destinationPath, resumeOffset, onBytesCopied, cancellationToken).ConfigureAwait(false);

                var info = new FileInfo(sourcePath);
                try
                {
                    File.SetLastWriteTimeUtc(destinationPath, info.LastWriteTimeUtc);
                    File.SetCreationTimeUtc(destinationPath, info.CreationTimeUtc);
                }
                catch (IOException)
                {
                    // Timestamp preservation is best-effort.
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                // Loop again: resume offset is recomputed from whatever was already flushed to disk.
            }
        }
    }

    private static long GetResumeOffset(string destinationPath, long sourceLength)
    {
        if (!File.Exists(destinationPath))
        {
            return 0;
        }

        var existingLength = new FileInfo(destinationPath).Length;
        return existingLength > 0 && existingLength <= sourceLength ? existingLength : 0;
    }

    private static async Task StreamCopyAsync(
        string sourcePath,
        string destinationPath,
        long resumeOffset,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

        source.Seek(resumeOffset, SeekOrigin.Begin);
        destination.SetLength(resumeOffset);
        destination.Seek(resumeOffset, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytesCopied(read);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
