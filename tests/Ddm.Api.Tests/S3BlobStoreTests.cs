using Amazon.Runtime;
using Amazon.S3;
using Ddm.Api.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Ddm.Api.Tests;

public class S3BlobStoreTests : IAsyncLifetime
{
    // RustFS: an S3-compatible MinIO drop-in (MinIO no longer publishes public container images).
    private readonly IContainer _minio = new ContainerBuilder("rustfs/rustfs:latest")
        .WithPortBinding(9000, true)
        .WithEnvironment("RUSTFS_ACCESS_KEY", "minioadmin")
        .WithEnvironment("RUSTFS_SECRET_KEY", "minioadmin")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/minio/health/live").ForPort(9000)))
        .Build();

    private IAmazonS3 _s3 = null!;
    private S3BlobStore _store = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        _s3 = new AmazonS3Client(new BasicAWSCredentials("minioadmin", "minioadmin"), new AmazonS3Config
        {
            ServiceURL = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        await S3BlobStore.EnsureBucketAsync(_s3, "ddm-test", default);
        _store = new S3BlobStore(_s3, "ddm-test");
    }

    public async Task DisposeAsync()
    {
        _s3.Dispose();
        await _minio.DisposeAsync();
    }

    [Fact]
    public async Task Put_then_get_round_trips_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("# Hello\n\nunicode: héllo ✓");
        await _store.PutAsync("projects/p/docs/abc.md", bytes, "text/markdown", default);
        Assert.Equal(bytes, await _store.GetAsync("projects/p/docs/abc.md", default));
    }

    [Fact]
    public async Task Missing_keys_return_null() => Assert.Null(await _store.GetAsync("nope/nothing.md", default));

    [Fact]
    public async Task Putting_the_same_key_twice_is_harmless()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await _store.PutAsync("k", bytes, "application/octet-stream", default);
        await _store.PutAsync("k", bytes, "application/octet-stream", default);
        Assert.Equal(bytes, await _store.GetAsync("k", default));
    }

    [Fact]
    public async Task A_one_mebibyte_payload_round_trips()
    {
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await _store.PutAsync("big", bytes, "application/octet-stream", default);
        Assert.Equal(bytes, await _store.GetAsync("big", default));
    }

    [Fact]
    public async Task Ensuring_the_bucket_twice_does_not_throw() =>
        await S3BlobStore.EnsureBucketAsync(_s3, "ddm-test", default);
}
