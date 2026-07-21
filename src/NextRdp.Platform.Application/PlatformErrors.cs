namespace NextRdp.Platform.Application;

public sealed class PlatformProblemException : Exception
{
    public PlatformProblemException(int statusCode, string code, string title, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Title = title;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public int StatusCode { get; }
    public string Code { get; }
    public string Title { get; }
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public static PlatformProblemException Validation(IEnumerable<string> errors) =>
        new(400, "validation_failed", "Validation failed", "One or more values are invalid.",
            new Dictionary<string, string[]> { ["request"] = errors.ToArray() });

    public static PlatformProblemException NotFound(string resource) =>
        new(404, "resource_not_found", "Resource not found", $"The requested {resource} was not found in the authorized scope.");

    public static PlatformProblemException Conflict(string detail) =>
        new(409, "revision_conflict", "Revision conflict", detail);

    public static PlatformProblemException Forbidden(string reason) =>
        new(403, "access_denied", "Access denied", reason);
}
