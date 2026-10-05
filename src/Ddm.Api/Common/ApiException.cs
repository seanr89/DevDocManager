namespace Ddm.Api.Common;

public sealed class ApiException(int status, string code, string title, string? detail = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    /// <summary>Extra problem-details members, such as an <c>errors</c> list.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

    public static ApiException BadRequest(string code, string title, string? detail = null) => new(400, code, title, detail);
    public static ApiException Forbidden(string code, string title) => new(403, code, title);
    public static ApiException NotFound(string code, string title) => new(404, code, title);
    public static ApiException Conflict(string code, string title) => new(409, code, title);
    public static ApiException PreconditionFailed(string title) => new(412, "precondition_failed", title);
    public static ApiException PreconditionRequired(string title) => new(428, "precondition_required", title);
    public static ApiException PayloadTooLarge(string title) => new(413, "payload_too_large", title);

    /// <summary>422 with every problem listed under <c>errors</c>, so a client can fix them all at once.</summary>
    public static ApiException Unprocessable(string code, string title, IReadOnlyList<object> errors, string? detail = null) =>
        new(422, code, title, detail) { Extensions = new Dictionary<string, object?> { ["errors"] = errors } };
}
