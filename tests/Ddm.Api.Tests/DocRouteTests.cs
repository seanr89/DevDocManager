using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class DocRouteTests
{
    [Fact] public void Plain_document() => Assert.Equal(new DocRoute.Current("guides/setup.md"), DocRoute.Parse("guides/setup.md"));
    [Fact] public void History() => Assert.Equal(new DocRoute.History("guides/setup.md"), DocRoute.Parse("guides/setup.md/versions"));
    [Fact] public void Snapshot() => Assert.Equal(new DocRoute.Snapshot("a.md", 3), DocRoute.Parse("a.md/versions/3"));
    [Fact] public void Restore() => Assert.Equal(new DocRoute.Restore("a.md", 3), DocRoute.Parse("a.md/versions/3/restore"));

    [Fact] public void A_document_named_versions_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("a/versions.md"), DocRoute.Parse("a/versions.md"));

    [Fact] public void A_directory_named_versions_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("versions/3.md"), DocRoute.Parse("versions/3.md"));

    [Fact] public void A_path_that_looks_like_a_suffix_but_ends_in_md_is_a_document() =>
        Assert.Equal(new DocRoute.Current("x.md/versions/3.md"), DocRoute.Parse("x.md/versions/3.md"));

    [Fact] public void Overflowing_version_numbers_become_0_and_so_404_later() =>
        Assert.Equal(new DocRoute.Snapshot("a.md", 0), DocRoute.Parse("a.md/versions/99999999999"));

    [Fact] public void Anything_else_falls_through_to_path_validation() =>
        Assert.Equal(new DocRoute.Current("a.md/versions/x"), DocRoute.Parse("a.md/versions/x"));

    [Fact] public void Tags() => Assert.Equal(new DocRoute.Tags("guides/setup.md"), DocRoute.Parse("guides/setup.md/tags"));
    [Fact] public void A_document_named_tags_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("a/tags.md"), DocRoute.Parse("a/tags.md"));
}
