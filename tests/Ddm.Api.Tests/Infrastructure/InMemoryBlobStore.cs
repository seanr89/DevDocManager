using System.Collections.Concurrent;
using Ddm.Api.Storage;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    /// <summary>When set, the next PutAsync throws once, simulating a storage outage.</summary>
    public bool FailNextPut { get; set; }
    public int Count => _blobs.Count;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (FailNextPut)
        {
            FailNextPut = false;
            throw new IOException("simulated storage outage");
        }
        _blobs[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult<byte[]?>(_blobs.TryGetValue(key, out var b) ? b : null);
}
