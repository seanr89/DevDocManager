using System.Text;
using System.Text.RegularExpressions;
using Ddm.Api.Common;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Specs;

public sealed record SpecError(string? Pointer, int? Line, int? Column, string Message);
public sealed record SpecOperation(string Method, string Path, string? OperationId, string? Summary, IReadOnlyList<string> Tags);
public sealed record ValidatedSpec(
    string Format, string OpenApiVersion, string Title, string ApiVersion, IReadOnlyList<SpecOperation> Operations, byte[] NormalizedJson);

/// <summary>
/// Validates an OpenAPI 3.0/3.1 document. Our own checks run first (UTF-8, alias and depth guard, version gate,
/// no external $ref) so the library never sees hostile input; then Microsoft.OpenApi parses and validates it.
/// Every problem is reported at once, each with the line and column it points at.
/// </summary>
public static partial class SpecValidator
{
    public const int MaxDepth = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex(@"^3\.[01]\.\d+\z")]
    private static partial Regex SupportedVersion();

    public static string FormatOf(string text) => text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('{') ? "json" : "yaml";

    public static async Task<ValidatedSpec> ValidateAsync(byte[] bytes, CancellationToken ct)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Specs must be valid UTF-8"); }
        var format = FormatOf(text);

        if (SafeYaml.Check(text, MaxDepth) is { } problem) throw Invalid([new(null, problem.Line, problem.Column, problem.Message)]);
        YamlNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1) throw Invalid([new(null, null, null, "A spec must be a single YAML or JSON document")]);
            root = stream.Documents[0].RootNode;
        }
        catch (YamlException ex) { throw Invalid([new(null, (int)ex.Start.Line, (int)ex.Start.Column, ex.Message)]); }
        if (root is not YamlMappingNode map) throw Invalid([At(root, null, "A spec must be a YAML or JSON object")]);

        var versionNode = map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "openapi" }).Value as YamlScalarNode;
        if (versionNode?.Value is not { } version || !SupportedVersion().IsMatch(version))
            throw ApiException.Unprocessable("unsupported_openapi_version", "Only OpenAPI 3.0 and 3.1 are supported",
                [At(versionNode ?? root, "#/openapi", $"Expected openapi: 3.0.x or 3.1.x, found '{versionNode?.Value ?? "nothing"}'")]);

        var errors = ExternalRefs(root, "#").ToList();
        if (errors.Count > 0) throw Invalid(errors);

        var settings = new OpenApiReaderSettings(); // LoadExternalRefs defaults to false
        settings.AddYamlReader();
        OpenApiDocument? doc;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var read = await OpenApiDocument.LoadAsync(ms, format, settings, ct);
            doc = read.Document;
            errors = (read.Diagnostic?.Errors ?? []).Select(e => ErrorAt(root, e.Pointer, e.Message)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ApiException)
        {
            throw Invalid([new(null, null, null, ex.Message)]);
        }
        if (errors.Count > 0) throw Invalid(errors);
        if (doc is null) throw Invalid([new(null, null, null, "The document could not be read as OpenAPI")]);

        return new(format, version, doc.Info?.Title ?? "", doc.Info?.Version ?? "", Operations(doc), Normalize(doc, version));
    }

    private static SpecError ErrorAt(YamlNode root, string? rawPointer, string message)
    {
        var pointer = PointerLocator.PointerFrom(rawPointer, message);
        var at = PointerLocator.Locate(root, pointer);
        return new(pointer, at?.Line, at?.Column, message);
    }

    /// <summary>Every $ref that does not start with '#'. Specs must be self-contained: resolving a URL or file is SSRF.</summary>
    private static IEnumerable<SpecError> ExternalRefs(YamlNode node, string pointer)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    var name = (key as YamlScalarNode)?.Value ?? "";
                    var child = $"{pointer}/{name.Replace("~", "~0").Replace("/", "~1")}";
                    if (name == "$ref" && value is YamlScalarNode { Value: { } target } && !target.StartsWith('#'))
                        yield return At(value, child, $"External $ref '{target}' is not allowed: specs must be self-contained");
                    else
                        foreach (var e in ExternalRefs(value, child)) yield return e;
                }
                break;
            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++)
                    foreach (var e in ExternalRefs(seq.Children[i], $"{pointer}/{i}")) yield return e;
                break;
        }
    }

    private static IReadOnlyList<SpecOperation> Operations(OpenApiDocument doc) =>
        (from path in doc.Paths ?? new OpenApiPaths()
         from op in path.Value.Operations ?? new()
         select new SpecOperation(
             op.Key.Method.ToLowerInvariant(), path.Key, op.Value.OperationId, op.Value.Summary,
             op.Value.Tags?.Select(t => t.Name ?? "").ToList() ?? [])).ToList();

    /// <summary>The parsed document re-serialised as JSON in its own OpenAPI version, with internal $refs kept (schemas may be recursive).</summary>
    private static byte[] Normalize(OpenApiDocument doc, string version)
    {
        using var writer = new StringWriter();
        var json = new OpenApiJsonWriter(writer);
        if (version.StartsWith("3.1", StringComparison.Ordinal)) doc.SerializeAsV31(json);
        else doc.SerializeAsV3(json);
        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static SpecError At(YamlNode node, string? pointer, string message) =>
        new(pointer, (int)node.Start.Line, (int)node.Start.Column, message);

    private static ApiException Invalid(IReadOnlyList<SpecError> errors) =>
        ApiException.Unprocessable("invalid_spec", "The OpenAPI document is not valid", errors, $"{errors.Count} problem(s) found");
}
