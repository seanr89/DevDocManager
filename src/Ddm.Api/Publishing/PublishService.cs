using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Specs;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Publishing;

/// <summary>
/// Mirrors an archive into a project in one transaction: validate every file, plan against the live state,
/// upload new blobs, then re-plan under the project lock and stage every change through the content services.
/// </summary>
public sealed class PublishService(DdmDbContext db, DocumentService docs, AssetService assets, SpecService specs)
{
    /// <summary>One archive file, validated and ready to stage. Exactly one of Doc, Asset and Spec is set.</summary>
    private sealed record Prepared(ItemKey Key, string Sha256, PreparedDocument? Doc, PreparedAsset? Asset, PreparedSpec? Spec);

    /// <summary>Every document, asset and spec row of a project (tombstones included) and the versions their pointers name.</summary>
    private sealed record Snapshot(
        Dictionary<string, Document> DocRows, Dictionary<string, Asset> AssetRows, Dictionary<string, Spec> SpecRows,
        Dictionary<Guid, ContentVersion> Versions)
    {
        public Dictionary<ItemKey, string> Live()
        {
            var live = new Dictionary<ItemKey, string>();
            foreach (var d in DocRows.Values.Where(d => d.DeletedAt is null && d.CurrentVersionId is not null))
                live[new(ItemType.Document, d.Path)] = Versions[d.CurrentVersionId!.Value].ContentSha256;
            foreach (var a in AssetRows.Values.Where(a => a.DeletedAt is null))
                live[new(ItemType.Asset, a.Path)] = a.Sha256;
            foreach (var s in SpecRows.Values.Where(s => s.DeletedAt is null && s.CurrentVersionId is not null))
                live[new(ItemType.Spec, s.Name)] = Versions[s.CurrentVersionId!.Value].ContentSha256;
            return live;
        }

        public DocumentState? DocStateAt(string path) =>
            DocRows.TryGetValue(path, out var d) ? new(d, d.CurrentVersionId is { } id ? Versions[id] : null) : null;

        public SpecState? SpecStateNamed(string name) =>
            SpecRows.TryGetValue(name, out var s) ? new(s, s.CurrentVersionId is { } id ? Versions[id] : null) : null;
    }

    public async Task<PublishResult> PublishAsync(Caller caller, Project project, ArchiveContents archive, PublishRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAllAsync(archive, request.Prefix, ct);
        var wanted = prepared.ToDictionary(p => p.Key, p => p.Sha256);

        var plan = PublishPlanner.Plan(wanted, (await LoadAsync(project, track: false, ct)).Live(), request.Prefix);
        if (plan.IsMassDelete && !request.AllowMassDelete)
            throw new ApiException(409, "publish_mass_delete", "This publish would delete most of the content in scope",
                $"It deletes {plan.Count(PlanAction.Delete)} of {plan.InScope} items under {ScopeName(request.Prefix)}. " +
                "Re-run with allowMassDelete=true if that is intended.");
        if (request.DryRun) return Result(plan, archive.Ignored, versions: null, dryRun: true);

        var byKey = prepared.ToDictionary(p => p.Key);
        foreach (var step in plan.Steps.Where(s => s.Action is PlanAction.Create or PlanAction.Update))
            await UploadAsync(project, byKey[new(step.Type, step.Key)], ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(project.Id, ct); // one publish per project at a time
        var snapshot = await LoadAsync(project, track: true, ct);
        // Plan again from the rows we are about to write over. If anything in scope moved, stop rather than guess:
        // the blobs uploaded above match the first plan only.
        if (!PublishPlanner.Plan(wanted, snapshot.Live(), request.Prefix).SameAs(plan)) throw Conflict();

        var versions = new Dictionary<ItemKey, int>();
        foreach (var step in plan.Steps.Where(s => s.Action != PlanAction.Unchanged))
            await StageAsync(caller, project, step, byKey, snapshot, request.Message, versions, ct);
        db.Audit(caller, project.Id, "publish",
            $"{ScopeName(request.Prefix)}: +{plan.Count(PlanAction.Create)} ~{plan.Count(PlanAction.Update)} -{plan.Count(PlanAction.Delete)}");
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation() || ex.IsForeignKeyViolation() || ex.IsDeadlock())
        {
            // A single-item write committed between our read and our save: the row versions or version numbers collided
            // (or a tag we staged was deleted, or Postgres broke a lock cycle against such a write).
            throw Conflict();
        }
        return Result(plan, archive.Ignored, versions, dryRun: false);
    }

    private async Task<List<Prepared>> PrepareAllAsync(ArchiveContents archive, string prefix, CancellationToken ct)
    {
        var result = new List<Prepared>();
        var errors = new List<PublishError>();
        var specSources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in archive.Files)
        {
            var path = prefix + file.Path;
            try
            {
                switch (PublishPlanner.Classify(file.Path, file.Content))
                {
                    case ItemType.Document:
                    {
                        var doc = DocumentService.Prepare(path, file.Content);
                        result.Add(new(new(ItemType.Document, path), doc.Sha256, doc, null, null));
                        break;
                    }
                    case ItemType.Asset:
                    {
                        var asset = assets.Prepare(path, file.Content);
                        result.Add(new(new(ItemType.Asset, path), asset.Sha256, null, asset, null));
                        break;
                    }
                    case ItemType.Spec:
                    {
                        var name = PublishPlanner.SpecNameFor(file.Path);
                        if (!specSources.TryAdd(name, path))
                        {
                            errors.Add(new(path, "duplicate_spec_name", $"{specSources[name]} already publishes the spec '{name}'"));
                            break;
                        }
                        var spec = await specs.PrepareAsync(name, file.Content, ct);
                        result.Add(new(new(ItemType.Spec, name), spec.Sha256, null, null, spec));
                        break;
                    }
                    default:
                        errors.Add(new(path, "unsupported_file", "Only markdown, OpenAPI specs and allow-listed asset types can be published"));
                        break;
                }
            }
            catch (ApiException ex) when (ex.Status < 500)
            {
                if (ex.Extensions?.GetValueOrDefault("errors") is IEnumerable<SpecError> specErrors)
                    errors.AddRange(specErrors.Select(e => new PublishError(path, ex.Code, e.Message, e.Line)));
                else
                    errors.Add(new(path, ex.Code, ex.Detail ?? ex.Title));
            }
        }
        if (errors.Count > 0)
            throw ApiException.Unprocessable("publish_invalid", "The archive cannot be published", errors, $"{errors.Count} problem(s) found");
        return result;
    }

    private async Task<Snapshot> LoadAsync(Project project, bool track, CancellationToken ct)
    {
        IQueryable<T> Rows<T>(DbSet<T> set) where T : class => track ? set : set.AsNoTracking();
        var pid = project.Id;
        var docRows = await Rows(db.Documents).Where(d => d.ProjectId == pid).ToDictionaryAsync(d => d.Path, StringComparer.Ordinal, ct);
        var assetRows = await Rows(db.Assets).Where(a => a.ProjectId == pid).ToDictionaryAsync(a => a.Path, StringComparer.Ordinal, ct);
        var specRows = await Rows(db.Specs).Where(s => s.ProjectId == pid).ToDictionaryAsync(s => s.Name, StringComparer.Ordinal, ct);
        var ids = docRows.Values.Select(d => d.CurrentVersionId).Concat(specRows.Values.Select(s => s.CurrentVersionId)).OfType<Guid>().ToList();
        var versions = await Rows(db.Versions).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        return new(docRows, assetRows, specRows, versions);
    }

    private async Task UploadAsync(Project project, Prepared p, CancellationToken ct)
    {
        if (p.Doc is { } doc) await docs.UploadAsync(project, doc, ct);
        else if (p.Asset is { } asset) await assets.UploadAsync(project, asset, ct);
        else await specs.UploadAsync(project, p.Spec!, ct);
    }

    private async Task StageAsync(
        Caller caller, Project project, PlanStep step, Dictionary<ItemKey, Prepared> byKey, Snapshot snapshot,
        string message, Dictionary<ItemKey, int> versions, CancellationToken ct)
    {
        var key = new ItemKey(step.Type, step.Key);
        var delete = step.Action == PlanAction.Delete;
        switch (step.Type)
        {
            case ItemType.Document when delete:
                docs.StageDelete(caller, project, snapshot.DocRows[step.Key]);
                break;
            case ItemType.Document:
                versions[key] = (await docs.StageAsync(caller, project, byKey[key].Doc!, snapshot.DocStateAt(step.Key), message, ct)).Version.Number;
                break;
            case ItemType.Asset when delete:
                assets.StageDelete(caller, project, snapshot.AssetRows[step.Key]);
                break;
            case ItemType.Asset:
                assets.Stage(caller, project, byKey[key].Asset!, snapshot.AssetRows.GetValueOrDefault(step.Key));
                break;
            case ItemType.Spec when delete:
                specs.StageDelete(caller, project, snapshot.SpecRows[step.Key]);
                break;
            case ItemType.Spec:
                versions[key] = specs.Stage(caller, project, byKey[key].Spec!, snapshot.SpecStateNamed(step.Key), message).Version.Number;
                break;
        }
    }

    private static PublishResult Result(PublishPlan plan, IReadOnlyList<string> ignored, Dictionary<ItemKey, int>? versions, bool dryRun)
    {
        List<PublishedItem> Of(PlanAction action) => plan.Steps
            .Where(s => s.Action == action)
            .Select(s => new PublishedItem(Wire.Lower(s.Type), s.Key,
                versions is not null && versions.TryGetValue(new(s.Type, s.Key), out var v) ? v : null))
            .ToList();
        return new(dryRun, Of(PlanAction.Create), Of(PlanAction.Update), Of(PlanAction.Delete), plan.Count(PlanAction.Unchanged), ignored);
    }

    private static string ScopeName(string prefix) => prefix.Length == 0 ? "/" : prefix;

    private static ApiException Conflict() =>
        new(409, "publish_conflict", "Content changed while publishing", "Another write touched content in scope during this publish; retry it");
}
