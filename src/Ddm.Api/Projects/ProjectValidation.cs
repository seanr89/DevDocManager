using System.Text.RegularExpressions;
using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Projects;

public static partial class ProjectValidation
{
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z")]
    private static partial Regex SlugRegex();

    public static ApiException Invalid(string detail) =>
        ApiException.BadRequest("validation_failed", "The request is not valid", detail);

    public static (string Slug, string Name, string Description, Visibility Visibility) ForCreate(CreateProjectRequest? r)
    {
        if (r is null) throw Invalid("A JSON body is required");
        var slug = r.Slug ?? "";
        if (!SlugRegex().IsMatch(slug))
            throw Invalid("slug must be 1-64 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen");
        var visibility = r.Visibility is null ? Visibility.Private : ParseVisibility(r.Visibility);
        return (slug, Name(r.Name), Description(r.Description), visibility);
    }

    public static string Name(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length is >= 1 and <= 200 ? n : throw Invalid("name must be 1-200 characters");
    }

    public static string Description(string? description)
    {
        var d = description?.Trim() ?? "";
        return d.Length <= 2000 ? d : throw Invalid("description must be at most 2000 characters");
    }

    public static Visibility ParseVisibility(string raw) =>
        Wire.TryParse<Visibility>(raw, out var v) ? v : throw Invalid("visibility must be 'private' or 'internal'");
}
