using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tags;

public sealed class TagService(DdmDbContext db)
{
    /// <summary>
    /// Stages replacing an item's tag set (names already normalised). Missing Tag rows are inserted at once with
    /// ON CONFLICT DO NOTHING, so concurrent writers never collide on a new name; inside a transaction they roll back
    /// with it, outside one a failed save leaves at most an unused tag. Assignments wait for the caller's SaveChanges.
    /// </summary>
    public async Task StageSetAsync(Guid projectId, ItemRef item, IReadOnlyList<string> names, CancellationToken ct)
    {
        foreach (var name in names)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"Tags\" (\"Id\", \"ProjectId\", \"Name\") VALUES ({Guid.NewGuid()}, {projectId}, {name}) ON CONFLICT (\"ProjectId\", \"Name\") DO NOTHING",
                ct);

        var wanted = await db.Tags.Where(t => t.ProjectId == projectId && names.Contains(t.Name)).Select(t => t.Id).ToListAsync(ct);
        var current = await db.TagAssignments.Where(a => a.ItemType == item.Type && a.ItemId == item.Id).ToListAsync(ct);
        db.TagAssignments.RemoveRange(current.Where(a => !wanted.Contains(a.TagId)));
        foreach (var tagId in wanted.Where(id => current.All(a => a.TagId != id)))
            db.TagAssignments.Add(new TagAssignment { TagId = tagId, ItemType = item.Type, ItemId = item.Id });
    }

    /// <summary>Replaces an item's tags from an API request and saves. Tags are metadata: no new version, no ETag change.</summary>
    public async Task<IReadOnlyList<string>> SetFromRequestAsync(
        Caller caller, Guid projectId, ItemRef item, string target, SetTagsRequest? body, CancellationToken ct)
    {
        if (body?.Tags is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "tags is required");
        var names = TagName.NormalizeSet(body.Tags);
        await StageSetAsync(projectId, item, names, ct);
        db.Audit(caller, projectId, "tags.set", target);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) when (IsTagRace(ex)) { throw TagsConflict(); }
        return names;
    }

    /// <summary>
    /// True when a failed save means a racing tag write: a unique violation is a racing insert, a concurrency
    /// exception a racing delete of the same assignment, and a foreign-key violation a tag removed between staging
    /// and saving.
    /// </summary>
    public static bool IsTagRace(Exception ex) =>
        ex is DbUpdateConcurrencyException
        || ex is DbUpdateException dbe && (dbe.IsUniqueViolation() || dbe.IsForeignKeyViolation());

    /// <summary>The 409 every racing tag write maps to.</summary>
    public static ApiException TagsConflict() =>
        ApiException.Conflict("tags_conflict", "The tags were changed concurrently; retry");

    /// <summary>Tag names for each item, ordered by name; items without tags map to an empty list.</summary>
    public async Task<Dictionary<Guid, IReadOnlyList<string>>> TagsForAsync(ItemType type, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await (from a in db.TagAssignments
                          join t in db.Tags on a.TagId equals t.Id
                          where a.ItemType == type && ids.Contains(a.ItemId)
                          select new { a.ItemId, t.Name }).ToListAsync(ct);
        var result = ids.Distinct().ToDictionary(id => id, _ => (IReadOnlyList<string>)Array.Empty<string>());
        foreach (var g in rows.GroupBy(r => r.ItemId))
            result[g.Key] = g.Select(r => r.Name).Order(StringComparer.Ordinal).ToList();
        return result;
    }

    public async Task<IReadOnlyList<string>> TagsForAsync(ItemRef item, CancellationToken ct) =>
        (await TagsForAsync(item.Type, [item.Id], ct))[item.Id];

    /// <summary>
    /// Ids of items carrying every one of <paramref name="names"/>. An item's tags all belong to its own project and
    /// names are unique per project, so each name matches at most one assignment per item and a count suffices.
    /// </summary>
    public IQueryable<Guid> ItemsWithAll(ItemType type, IReadOnlyList<string> names)
    {
        var count = names.Count;
        return from a in db.TagAssignments
               join t in db.Tags on a.TagId equals t.Id
               where a.ItemType == type && names.Contains(t.Name)
               group a by a.ItemId into g
               where g.Count() == count
               select g.Key;
    }
}
