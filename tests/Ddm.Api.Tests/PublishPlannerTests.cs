using Ddm.Api.Domain;
using Ddm.Api.Publishing;

namespace Ddm.Api.Tests;

public class PublishPlannerTests
{
    private static ItemKey Doc(string path) => new(ItemType.Document, path);
    private static ItemKey Asset(string path) => new(ItemType.Asset, path);
    private static ItemKey Spec(string name) => new(ItemType.Spec, name);
    private static Dictionary<ItemKey, string> Map(params (ItemKey Key, string Sha)[] items) => items.ToDictionary(i => i.Key, i => i.Sha);
    private static PlanAction ActionOf(PublishPlan plan, string key) => plan.Steps.Single(s => s.Key == key).Action;

    [Fact]
    public void Creates_updates_unchanged_and_deletes()
    {
        var plan = PublishPlanner.Plan(
            Map((Doc("new.md"), "1"), (Doc("same.md"), "2"), (Doc("changed.md"), "3")),
            Map((Doc("same.md"), "2"), (Doc("changed.md"), "old"), (Doc("gone.md"), "4")), "");
        Assert.Equal(PlanAction.Create, ActionOf(plan, "new.md"));
        Assert.Equal(PlanAction.Unchanged, ActionOf(plan, "same.md"));
        Assert.Equal(PlanAction.Update, ActionOf(plan, "changed.md"));
        Assert.Equal(PlanAction.Delete, ActionOf(plan, "gone.md"));
        Assert.Equal(3, plan.InScope);
    }

    [Fact]
    public void A_prefix_limits_deletes_to_its_subtree_and_never_deletes_specs()
    {
        var plan = PublishPlanner.Plan(
            Map((Doc("guides/a.md"), "1")),
            Map((Doc("guides/a.md"), "1"), (Doc("guides/old.md"), "2"), (Doc("other/keep.md"), "3"),
                (Spec("api"), "4"), (Asset("guides/img.png"), "5")),
            "guides/");
        Assert.Equal(["guides/img.png", "guides/old.md"],
            plan.Steps.Where(s => s.Action == PlanAction.Delete).Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Equal(3, plan.InScope);
    }

    [Fact]
    public void A_full_publish_deletes_specs_missing_from_the_archive()
    {
        var plan = PublishPlanner.Plan(Map((Doc("a.md"), "1")), Map((Doc("a.md"), "1"), (Spec("api"), "2")), "");
        Assert.Equal(PlanAction.Delete, ActionOf(plan, "api"));
    }

    [Theory]
    [InlineData(1, 1, true)]    // everything in scope
    [InlineData(10, 6, true)]   // more than half of 10+
    [InlineData(10, 5, false)]  // exactly half
    [InlineData(9, 8, false)]   // scopes under 10 only trip when everything goes
    [InlineData(4, 0, false)]
    [InlineData(0, 0, false)]
    public void Mass_delete_guard(int inScope, int deletes, bool expected)
    {
        var live = Map(Enumerable.Range(0, inScope).Select(i => (Doc($"d{i}.md"), "x")).ToArray());
        var wanted = Map(Enumerable.Range(deletes, inScope - deletes).Select(i => (Doc($"d{i}.md"), "x")).ToArray());
        Assert.Equal(expected, PublishPlanner.Plan(wanted, live, "").IsMassDelete);
    }

    [Fact]
    public void Plans_from_equal_inputs_are_the_same()
    {
        var wanted = Map((Doc("b.md"), "1"), (Asset("a.png"), "2"));
        var live = Map((Doc("c.md"), "3"));
        Assert.True(PublishPlanner.Plan(wanted, live, "").SameAs(PublishPlanner.Plan(wanted, live, "")));
        Assert.False(PublishPlanner.Plan(wanted, live, "").SameAs(PublishPlanner.Plan(wanted, Map((Doc("b.md"), "1")), "")));
    }

    [Theory]
    [InlineData("a.md", "# x", ItemType.Document)]
    [InlineData("api.yaml", "openapi: 3.0.3\ninfo: {}\n", ItemType.Spec)]
    [InlineData("api.yml", "---\nopenapi: 3.1.0\n", ItemType.Spec)]
    [InlineData("api.json", "{\"info\": {}, \"openapi\": \"3.0.0\"}", ItemType.Spec)]
    [InlineData("config.yaml", "name: x\nnested:\n  openapi: 3\n", ItemType.Asset)]
    [InlineData("data.json", "{\"a\": {\"openapi\": 1}}", ItemType.Asset)]
    [InlineData("logo.png", "", ItemType.Asset)]
    public void Classifies_files(string path, string content, ItemType expected) =>
        Assert.Equal(expected, PublishPlanner.Classify(path, Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData("api.yaml", "openapi: 3.0.3\ninfo: {}\n", ItemType.Spec)]
    [InlineData("api.json", "{\"info\": {}, \"openapi\": \"3.0.0\"}", ItemType.Spec)]
    [InlineData("api.json", "{\"openapi\": ", ItemType.Spec)]
    [InlineData("config.yaml", "name: x\nnested:\n  openapi: 3\n", ItemType.Asset)]
    [InlineData("a.md", "# x", ItemType.Document)]
    public void A_utf8_byte_order_mark_does_not_change_classification(string path, string content, ItemType expected) =>
        Assert.Equal(expected, PublishPlanner.Classify(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(content)]));

    [Theory] [InlineData("app.exe")] [InlineData("Makefile")]
    public void Unsupported_files_classify_as_null(string path) => Assert.Null(PublishPlanner.Classify(path, [1]));

    [Fact] public void Spec_names_come_from_the_lowercased_file_stem() =>
        Assert.Equal("payments.v2", PublishPlanner.SpecNameFor("apis/Payments.v2.yaml"));
}
