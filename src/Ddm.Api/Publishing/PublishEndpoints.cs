using System.Security.Claims;
using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Specs;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Publishing;

public static class PublishEndpoints
{
    public static void MapPublish(this RouteGroupBuilder v1) => v1.MapPost("/projects/{slug}/publish", PublishAsync);

    private static async Task<IResult> PublishAsync(
        string slug, string? prefix, bool? dryRun, bool? allowMassDelete, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, PublishService publisher, AssetService assets, SpecService specs,
        IOptions<PublishOptions> options, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var format = http.Request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant() switch
        {
            "application/gzip" or "application/x-gzip" or "application/x-tar+gzip" => ArchiveFormat.TarGz,
            "application/zip" => ArchiveFormat.Zip,
            _ => throw new ApiException(415, "unsupported_archive", "Send a tar.gz (application/gzip) or zip (application/zip) archive"),
        };
        prefix ??= "";
        if (ContentPath.ValidateFolder(prefix) is { } error) throw ApiException.BadRequest("invalid_path", "Invalid prefix", error);
        var message = DocumentEndpoints.MessageFrom(http.Request) ?? "Publish";
        var o = options.Value;

        var maxEntry = Math.Max(DocumentService.MaxBytes, Math.Max(assets.MaxBytes, specs.MaxBytes));
        ArchiveContents archive;
        // Spool the upload to a temp file so a 100 MB archive never sits in memory whole. The file is deleted when the
        // stream closes (and the block ends before any database work), so every exit path removes it.
        await using (var spool = await SpoolAsync(http, o, ct))
            archive = await ArchiveReader.ReadAsync(spool, format, new ArchiveLimits(o.MaxExpandedBytes, o.MaxEntries, maxEntry), ct);
        var result = await publisher.PublishAsync(caller, access.Project, archive,
            new PublishRequest(prefix, message, dryRun == true, allowMassDelete == true), ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Copies the request body into a temp file, refusing with 413 archive_too_large as soon as it passes the limit,
    /// whatever Content-Length claimed. Kestrel's own limit (set to the same value) fires mid-read on chunked or lying
    /// bodies; that BadHttpRequestException is mapped to the same problem code here.
    /// </summary>
    private static async Task<FileStream> SpoolAsync(HttpContext http, PublishOptions o, CancellationToken ct)
    {
        RequestBody.AllowUpTo(http, o.MaxArchiveBytes);
        if (http.Request.ContentLength > o.MaxArchiveBytes) throw TooLarge(o);
        var spool = new FileStream(Path.Combine(Path.GetTempPath(), $"ddm-publish-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > o.MaxArchiveBytes) throw TooLarge(o);
                await spool.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            spool.Position = 0;
            return spool;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await spool.DisposeAsync();
            throw TooLarge(o);
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    private static ApiException TooLarge(PublishOptions o) =>
        new(413, "archive_too_large", "The archive is too large", $"Archives are limited to {o.MaxArchiveBytes} bytes");
}
