namespace Ddm.Api.Publishing;

public sealed record PublishRequest(string Prefix, string Message, bool DryRun, bool AllowMassDelete);

/// <summary>Version is the new version number for documents and specs; null for assets and in a dry run.</summary>
public sealed record PublishedItem(string Type, string Key, int? Version);

public sealed record PublishResult(
    bool DryRun, IReadOnlyList<PublishedItem> Created, IReadOnlyList<PublishedItem> Updated, IReadOnlyList<PublishedItem> Deleted,
    int Unchanged, IReadOnlyList<string> Ignored);
