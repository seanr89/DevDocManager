using Ddm.Api.Common;
using Ddm.Api.Specs;

namespace Ddm.Api.Tests;

public class SpecValidatorTests
{
    private const string Pets = """
        openapi: 3.0.3
        info:
          title: Pets
          version: "1.2"
        paths:
          /pets:
            get:
              operationId: listPets
              summary: List pets
              tags: [pets]
              responses:
                '200':
                  description: ok
        """;

    private static Task<ValidatedSpec> Validate(string text) => SpecValidator.ValidateAsync(Encoding.UTF8.GetBytes(text), default);

    private static async Task<(string Code, JsonElement Errors)> FailAsync(string text)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(text));
        var errors = JsonSerializer.SerializeToElement(ex.Extensions?["errors"], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (ex.Code, errors);
    }

    [Fact]
    public async Task A_valid_yaml_spec_yields_its_summary_and_normalised_json()
    {
        var spec = await Validate(Pets);
        Assert.Equal(("yaml", "3.0.3", "Pets", "1.2"), (spec.Format, spec.OpenApiVersion, spec.Title, spec.ApiVersion));
        var op = Assert.Single(spec.Operations);
        Assert.Equal(("get", "/pets", "listPets", "List pets"), (op.Method, op.Path, op.OperationId, op.Summary));
        Assert.Equal(["pets"], op.Tags);
        using var json = JsonDocument.Parse(spec.NormalizedJson);
        Assert.Equal("Pets", json.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_json_3_1_spec_is_accepted_and_stays_3_1()
    {
        var spec = await Validate("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}");
        Assert.Equal("json", spec.Format);
        using var json = JsonDocument.Parse(spec.NormalizedJson);
        Assert.StartsWith("3.1", json.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task A_yaml_spec_with_a_leading_byte_order_mark_is_accepted()
    {
        var spec = await Validate("\uFEFF" + Pets);
        Assert.Equal(("yaml", "3.0.3", "Pets"), (spec.Format, spec.OpenApiVersion, spec.Title));
    }

    [Fact]
    public async Task A_json_spec_with_a_leading_byte_order_mark_is_accepted()
    {
        var spec = await Validate("\uFEFF{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}");
        Assert.Equal(("json", "3.1.0", "J"), (spec.Format, spec.OpenApiVersion, spec.Title));
    }

    [Theory]
    [InlineData("3.0.300000000000000000")] // longer than the varchar(16) column
    [InlineData("3.0.1234567")]
    [InlineData("3.0.\u0663")] // an Arabic-Indic digit: \d matches it, the version gate must not
    public async Task Openapi_versions_with_an_unbounded_or_non_ascii_patch_are_unsupported(string version)
    {
        var (code, _) = await FailAsync($"openapi: \"{version}\"\ninfo: {{title: x, version: '1'}}\npaths: {{}}\n");
        Assert.Equal("unsupported_openapi_version", code);
    }

    [Theory]
    [InlineData("3.0.3")]
    [InlineData("3.1.0")]
    [InlineData("3.0.123456")]
    public async Task Ordinary_openapi_versions_still_pass(string version)
    {
        var spec = await Validate($"openapi: {version}\ninfo: {{title: x, version: '1'}}\npaths: {{}}\n");
        Assert.Equal(version, spec.OpenApiVersion);
    }

    [Fact]
    public async Task An_unsupported_version_reports_its_errors_as_spec_errors()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate("openapi: 2.0\ninfo: {title: x, version: '1'}\npaths: {}\n"));
        var error = Assert.Single(Assert.IsAssignableFrom<IEnumerable<SpecError>>(ex.Extensions!["errors"]));
        Assert.Equal(1, error.Line);
        Assert.Contains("found '2.0'", error.Message);
    }

    [Fact]
    public async Task Swagger_2_is_unsupported()
    {
        var (code, errors) = await FailAsync("swagger: '2.0'\ninfo: {title: x, version: '1'}\npaths: {}\n");
        Assert.Equal("unsupported_openapi_version", code);
        Assert.Equal(1, errors.GetArrayLength());
    }

    // Spec review focus 9
    [Fact]
    public async Task Every_error_is_reported_with_its_line()
    {
        var (code, errors) = await FailAsync("""
            openapi: 3.0.3
            info:
              version: "1"
            paths:
              /a:
                get:
                  responses: {}
            """);
        Assert.Equal("invalid_spec", code);
        Assert.True(errors.GetArrayLength() >= 2);
        var responses = errors.EnumerateArray().Single(e => e.GetProperty("pointer").GetString() == "#/paths/~1a/get/responses");
        Assert.Equal(7, responses.GetProperty("line").GetInt32());
    }

    // Spec review focus 9
    [Fact]
    public async Task Errors_in_json_specs_carry_lines_too()
    {
        var (_, errors) = await FailAsync(
            "{\n  \"openapi\": \"3.0.3\",\n  \"info\": {\"title\": \"t\", \"version\": \"1\"},\n  \"paths\": {\n    \"/a\": {\"get\": {\"responses\": {}}}\n  }\n}");
        Assert.Equal(5, errors[0].GetProperty("line").GetInt32());
    }

    // Spec review focus 4
    [Fact]
    public async Task External_refs_are_refused_at_their_location()
    {
        var (code, errors) = await FailAsync("""
            openapi: 3.0.3
            info: {title: t, version: "1"}
            paths:
              /a:
                get:
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            $ref: 'https://evil.example/pet.yaml'
            """);
        Assert.Equal("invalid_spec", code);
        var e = Assert.Single(errors.EnumerateArray());
        Assert.Equal(12, e.GetProperty("line").GetInt32());
        Assert.Contains("evil.example", e.GetProperty("message").GetString());
    }

    // Spec review focus 4
    [Fact]
    public async Task Yaml_alias_bombs_are_refused_before_parsing()
    {
        var (code, errors) = await FailAsync("openapi: 3.0.3\na: &a [x, x]\nb: &b [*a, *a]\nc: [*b, *b]\n");
        Assert.Equal("invalid_spec", code);
        Assert.Contains("aliases", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Yaml_syntax_errors_report_a_line()
    {
        var (code, errors) = await FailAsync("openapi: 3.0.3\ninfo: {title: t, version: '1'\npaths: {}\n");
        Assert.Equal("invalid_spec", code);
        Assert.True(errors[0].GetProperty("line").GetInt32() >= 2);
    }

    [Fact] public async Task A_yaml_list_is_not_a_spec() => Assert.Equal("invalid_spec", (await FailAsync("- a\n- b\n")).Code);

    [Fact]
    public async Task Non_utf8_is_a_400() =>
        Assert.Equal("invalid_encoding", (await Assert.ThrowsAsync<ApiException>(() => SpecValidator.ValidateAsync([0xC3, 0x28], default))).Code);
}
