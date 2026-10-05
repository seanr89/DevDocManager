using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class SafeYamlTests
{
    [Fact] public void Plain_yaml_passes() => Assert.Null(SafeYaml.Check("a: 1\nb: [x, y]\n", 10));

    [Fact] public void Json_is_yaml_too() => Assert.Null(SafeYaml.Check("{\"a\": [1, {\"b\": 2}]}", 10));

    [Fact]
    public void Aliases_are_rejected_with_their_line()
    {
        var p = SafeYaml.Check("a: &x [1, 2]\nb: *x\n", 10);
        Assert.NotNull(p);
        Assert.Contains("aliases", p!.Message);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void Nesting_beyond_the_limit_is_rejected()
    {
        var p = SafeYaml.Check("a: {b: {c: {d: 1}}}\n", 3);
        Assert.NotNull(p);
        Assert.Contains("nested deeper than 3", p!.Message);
    }

    [Fact]
    public void Syntax_errors_report_a_line()
    {
        var p = SafeYaml.Check("a: 1\nb: [1,\n", 10);
        Assert.NotNull(p);
        Assert.NotNull(p!.Line);
    }

    [Fact]
    public void An_unterminated_flow_mapping_is_a_problem_not_an_exception()
    {
        var p = SafeYaml.Check("openapi: 3.0.3\ninfo: {title: t, version: '1'\npaths: {}\n", 10);
        Assert.NotNull(p);
        Assert.True(p!.Line >= 2);
    }
}
