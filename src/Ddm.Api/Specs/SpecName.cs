using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Specs;

public static partial class SpecName
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}\z")]
    private static partial Regex Valid();

    public static bool IsValid(string? name) => name is not null && Valid().IsMatch(name);

    public static string Require(string? name) =>
        IsValid(name)
            ? name!
            : throw ApiException.BadRequest("invalid_spec_name", "Invalid spec name",
                "Spec names are 1-100 characters: lowercase letters, digits, '.', '_' and '-', starting with a letter or digit");
}
