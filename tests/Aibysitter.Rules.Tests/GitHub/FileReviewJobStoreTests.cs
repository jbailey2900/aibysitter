using Aibysitter.Web.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aibysitter.Rules.Tests.GitHub;

public sealed class FileReviewJobStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aib-queue-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Save_ThenFind_ReturnsPendingJob()
    {
        var store = Store();
        var job = Job();

        store.Save(job);

        Assert.Equal(StoredJobState.Pending, store.Find(job.DeliveryId, out var found));
        Assert.Equal(job, found);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("8f14e45f-ceea-467f-a0e6-0d1c7a9b3e21")]
    public void Find_UnknownOrInvalidDelivery_ReturnsNone(string? deliveryId)
    {
        Assert.Equal(StoredJobState.None, Store().Find(deliveryId, out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Save_NonGuidDelivery_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store().Save(Job() with { DeliveryId = "delivery-1" }));
    }

    [Fact]
    public void Save_SameDeliveryTwice_Throws()
    {
        var store = Store();
        var job = Job();
        store.Save(job);

        Assert.ThrowsAny<IOException>(() => store.Save(job));
    }

    [Fact]
    public void Lease_CountsAttempts_AndBlocksSecondClaim()
    {
        var store = Store();
        var job = Job();
        store.Save(job);

        using (var first = store.TryAcquire(job))
        {
            Assert.Equal(1, first!.Attempts);
            Assert.Null(store.TryAcquire(job));
            Assert.Equal(StoredJobState.InProgress, store.Find(job.DeliveryId, out _));
        }

        using var second = store.TryAcquire(job);
        Assert.Equal(2, second!.Attempts);
    }

    [Fact]
    public void Complete_RemovesJob()
    {
        var store = Store();
        var job = Job();
        store.Save(job);

        store.TryAcquire(job)!.Complete();

        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(StoredJobState.None, store.Find(job.DeliveryId, out _));
        Assert.Null(store.TryAcquire(job));
    }

    [Fact]
    public void EmptyFile_IsCompleted_AndDeleted()
    {
        var store = Store();
        var job = Job();
        File.WriteAllText(Path.Combine(root, job.DeliveryId + ".json"), "");

        Assert.Empty(store.LoadPending());
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void LoadPending_OldestFirst_SkipsClaimed()
    {
        var store = Store();
        var (a, b, c) = (Job(), Job(), Job());
        foreach (var job in new[] { b, a, c })
        {
            store.Save(job);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        using var claimed = store.TryAcquire(a);

        Assert.Equal(new[] { b, c }, store.LoadPending());
    }

    [Fact]
    public void LoadPending_UnreadableFile_MovesToFailed()
    {
        var store = Store();
        var name = Guid.NewGuid().ToString("D") + ".json";
        File.WriteAllText(Path.Combine(root, name), "{ not json");

        Assert.Empty(store.LoadPending());
        Assert.True(File.Exists(Path.Combine(root, FileReviewJobStore.FailedFolder, name)));
        Assert.False(File.Exists(Path.Combine(root, name)));
    }

    [Fact]
    public void LoadPending_DeletesOnlyStaleTempFiles()
    {
        var store = Store();
        var stale = Path.Combine(root, Guid.NewGuid().ToString("D") + ".json.tmp");
        var fresh = Path.Combine(root, Guid.NewGuid().ToString("D") + ".json.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, time.GetUtcNow().UtcDateTime.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, time.GetUtcNow().UtcDateTime.AddMinutes(-5));

        store.LoadPending();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void LoadPending_DeletesFailedJobFilesOlderThan14Days()
    {
        var store = Store();
        var failed = Path.Combine(root, FileReviewJobStore.FailedFolder);
        var old = Path.Combine(failed, Guid.NewGuid().ToString("D") + ".json");
        var recent = Path.Combine(failed, Guid.NewGuid().ToString("D") + ".json");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(old, time.GetUtcNow().UtcDateTime.AddDays(-15));
        File.SetLastWriteTimeUtc(recent, time.GetUtcNow().UtcDateTime.AddDays(-13));

        store.LoadPending();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(TimeSpan.FromDays(14), FileReviewJobStore.FailedRetention);
    }

    [Fact]
    public void SecondStoreInstance_SeesSavedJobs()
    {
        var job = Job();
        Store().Save(job);

        Assert.Equal(new[] { job }, Store().LoadPending());
    }

    private FileReviewJobStore Store() => new(root, time, NullLogger<FileReviewJobStore>.Instance);

    private static ReviewJob Job() => new(new PullRequestRef(42, "o", "r", 7, "abcdef0123"), 99, Guid.NewGuid().ToString("D"));

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan by) => current += by;
    }
}
