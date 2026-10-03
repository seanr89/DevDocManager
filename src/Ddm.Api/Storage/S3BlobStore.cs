using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Ddm.Api.Storage;

public sealed class S3BlobStore(IAmazonS3 s3, string bucket) : IBlobStore
{
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var body = new MemoryStream(content, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body, ContentType = contentType }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, ct);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public static async Task EnsureBucketAsync(IAmazonS3 s3, string bucket, CancellationToken ct)
    {
        try { await s3.PutBucketAsync(bucket, ct); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }
}
