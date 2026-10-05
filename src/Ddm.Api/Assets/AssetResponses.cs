using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Storage;
using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Assets;

/// <summary>Streams stored bytes with the headers that keep untrusted content inert (shared by the API and /content).</summary>
public static class AssetResponses
{
    public const string Csp = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    public static async Task<IResult> ServeAsync(
        HttpContext http, IBlobStore blobs, string storageKey, string contentType, long size, string sha256,
        string fileName, string cacheControl, CancellationToken ct)
    {
        var etag = Preconditions.ShaETag(sha256);
        var headers = http.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = cacheControl;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = Csp;
        if (!contentType.StartsWith("image/", StringComparison.Ordinal))
            headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();

        if (http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Tag.Value == etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = contentType;
            http.Response.ContentLength = size;
            return Results.Empty;
        }

        var stream = await blobs.OpenReadAsync(storageKey, ct)
                     ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {storageKey}");
        http.Response.ContentLength = size;
        return Results.Stream(stream, contentType);
    }

    public static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
