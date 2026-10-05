using Microsoft.AspNetCore.Http.Features;

namespace Ddm.Api.Common;

public static class RequestBody
{
    /// <summary>Raises the server's body-size limit for this request (Kestrel's default is 30 MB).</summary>
    public static void AllowUpTo(HttpContext http, long bytes)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = bytes;
    }

    /// <summary>Reads the whole body, failing with 413 as soon as it passes <paramref name="max"/>, whatever Content-Length claimed.</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpRequest request, long max, string tooLargeMessage, CancellationToken ct)
    {
        if (request.ContentLength > max) throw ApiException.PayloadTooLarge(tooLargeMessage);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) throw ApiException.PayloadTooLarge(tooLargeMessage);
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}
