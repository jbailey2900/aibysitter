using System.Text.Json;

namespace Aibysitter.Web.GitHub;

/// <summary>
/// One JSON file per job, <c>{deliveryId}.json</c>. A lease holds the file open exclusively, so a second process
/// (IIS overlapped recycle) sees it as in progress. Completing truncates the file under the lock before deleting it;
/// an empty file is a completed job wherever it is found.
/// </summary>
public sealed class FileReviewJobStore : IReviewJobStore
{
    public const string FailedFolder = "failed";
    private const string Extension = ".json";
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>Unreadable job files in <see cref="FailedFolder"/> older than this are deleted at startup.</summary>
    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(14);

    private readonly string root;
    private readonly TimeProvider time;
    private readonly ILogger<FileReviewJobStore> logger;

    public FileReviewJobStore(string path, TimeProvider time, ILogger<FileReviewJobStore> logger)
    {
        root = Path.GetFullPath(path);
        this.time = time;
        this.logger = logger;
        Directory.CreateDirectory(Path.Combine(root, FailedFolder));
    }

    public bool IsDurable => true;

    public StoredJobState Find(string? deliveryId, out ReviewJob? job)
    {
        job = null;
        if (!Guid.TryParse(deliveryId, out var id) || !File.Exists(PathFor(id)))
        {
            return StoredJobState.None;
        }

        try
        {
            job = Peek(PathFor(id))?.Job;
            return job is null ? StoredJobState.None : StoredJobState.Pending;
        }
        catch (FileNotFoundException)
        {
            return StoredJobState.None;
        }
        catch (IOException)
        {
            return StoredJobState.InProgress;
        }
    }

    public void Save(ReviewJob job)
    {
        var path = PathFor(IdOf(job));
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredJob(job, 0, time.GetUtcNow())));
        File.Move(temp, path, overwrite: false);
    }

    public IReviewJobLease? TryAcquire(ReviewJob job)
    {
        FileStream? stream = null;
        try
        {
            var path = PathFor(IdOf(job));
            stream = OpenExclusive(path);
            if (Read(stream) is not { } stored)
            {
                stream.Dispose();
                DeleteIfEmpty(path);
                return null;
            }

            var counted = stored with { Attempts = stored.Attempts + 1 };
            Write(stream, counted);
            return new Lease(stream, counted.Attempts);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            stream?.Dispose();
            return null;
        }
    }

    public IReadOnlyList<ReviewJob> LoadPending()
    {
        DeleteExpiredFailedFiles();
        DeleteStaleTempFiles();
        return Directory.EnumerateFiles(root, "*" + Extension)
            .Select(TryPeek)
            .OfType<StoredJob>()
            .OrderBy(s => s.QueuedAt)
            .Select(s => s.Job)
            .ToList();
    }

    /// <summary>Stored job at <paramref name="path"/>. Null when completed or unreadable. Throws <see cref="IOException"/> when claimed.</summary>
    private StoredJob? Peek(string path)
    {
        StoredJob? stored;
        try
        {
            using var stream = OpenExclusive(path);
            stored = Read(stream);
        }
        catch (JsonException ex)
        {
            MoveToFailed(path, ex);
            return null;
        }

        if (stored is null)
        {
            DeleteIfEmpty(path);
        }

        return stored;
    }

    private StoredJob? TryPeek(string path)
    {
        try
        {
            return Peek(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void MoveToFailed(string path, Exception ex)
    {
        logger.LogError(ex, "Review job file {File} is unreadable; moved to {Folder}", Path.GetFileName(path), FailedFolder);
        try
        {
            File.Move(path, Path.Combine(root, FailedFolder, Path.GetFileName(path)), overwrite: true);
        }
        catch (IOException moveEx)
        {
            logger.LogError(moveEx, "Could not move {File} to {Folder}", Path.GetFileName(path), FailedFolder);
        }
    }

    private void DeleteExpiredFailedFiles()
    {
        var cutoff = time.GetUtcNow() - FailedRetention;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, FailedFolder)).Where(f => File.GetLastWriteTimeUtc(f) < cutoff.UtcDateTime))
        {
            TryDelete(file);
        }
    }

    private void DeleteStaleTempFiles()
    {
        var cutoff = time.GetUtcNow() - StaleTempAge;
        foreach (var temp in Directory.EnumerateFiles(root, "*" + Extension + ".tmp").Where(t => File.GetLastWriteTimeUtc(t) < cutoff.UtcDateTime))
        {
            TryDelete(temp);
        }
    }

    /// <summary>Stored job, or null for an empty (completed) file.</summary>
    private static StoredJob? Read(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return null;
        }

        stream.Position = 0;
        return JsonSerializer.Deserialize<StoredJob>(stream) ?? throw new JsonException("Empty job document.");
    }

    private static void Write(FileStream stream, StoredJob stored)
    {
        stream.SetLength(0);
        JsonSerializer.Serialize(stream, stored);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenExclusive(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static void DeleteIfEmpty(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Length == 0)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held by another process; it deletes the file itself.
        }
    }

    private static Guid IdOf(ReviewJob job) =>
        Guid.TryParse(job.DeliveryId, out var id) ? id : throw new ArgumentException($"Delivery ID \"{job.DeliveryId}\" is not a GUID.", nameof(job));

    private string PathFor(Guid id) => Path.Combine(root, id.ToString("D") + Extension);

    private sealed record StoredJob(ReviewJob Job, int Attempts, DateTimeOffset QueuedAt);

    private sealed class Lease(FileStream stream, int attempts) : IReviewJobLease
    {
        public int Attempts => attempts;

        public void Complete()
        {
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
            var path = stream.Name;
            stream.Dispose();
            TryDelete(path);
        }

        public void Dispose() => stream.Dispose();
    }
}
