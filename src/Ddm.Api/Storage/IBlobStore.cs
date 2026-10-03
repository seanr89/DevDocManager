namespace Ddm.Api.Storage;

public interface IBlobStore
{
    /// <summary>Stores the object. Keys are content-addressed, so re-putting the same key is harmless.</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Returns null when the key does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
}
