namespace Ddm.Api.Domain;

/// <summary>Any taggable thing: a document, asset, spec or project.</summary>
public readonly record struct ItemRef(ItemType Type, Guid Id);
