namespace Ddm.Api.Publishing;

public sealed class PublishOptions
{
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;
    public long MaxExpandedBytes { get; set; } = 250L * 1024 * 1024;
    public int MaxEntries { get; set; } = 5000;
}
