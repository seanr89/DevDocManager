namespace Ddm.Api.Common;

public static class ProblemCodes
{
    public static string ForStatus(int status) => status switch
    {
        400 => "bad_request",
        401 => "unauthenticated",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        406 => "not_acceptable",
        409 => "conflict",
        412 => "precondition_failed",
        413 => "payload_too_large",
        415 => "unsupported_media_type",
        428 => "precondition_required",
        >= 500 => "internal_error",
        _ => $"http_{status}",
    };
}
