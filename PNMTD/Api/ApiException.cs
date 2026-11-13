namespace PNMTD.Api;

/// <summary>
/// A v1 error with its HTTP status and the stable <c>code</c> of the ProblemDetails body
/// (docs/webui-migration-plan.md §3.7). Thrown by the domain services, written by <see cref="ApiExceptionFilter"/>.
/// </summary>
public sealed class ApiException(int status, string code, string detail, IDictionary<string, string[]>? errors = null)
    : Exception(detail)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, $"{what} not found.");

    public static ApiException Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, code, detail);

    public static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] });
}

public static class ApiErrorCodes
{
    public const string NotFound = "not_found";
    public const string ValidationFailed = "validation_failed";

    /// <summary>An encrypted backup PNMTD cannot read: the browser decrypts and compares it (docs/mikrotik-backup-plan.md §3.6).</summary>
    public const string BackupEncrypted = "backup_encrypted";
    public const string DuplicateKey = "duplicate_key";
    public const string ReadOnly = "read_only";
    public const string Unauthorized = "unauthorized";
    public const string ForbiddenRole = "forbidden_role";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InternalError = "internal_error";
    public const string Error = "error";

    public static string ForStatus(int status) => status switch
    {
        400 => ValidationFailed,
        401 => Unauthorized,
        403 => ForbiddenRole,
        404 => NotFound,
        405 => MethodNotAllowed,
        409 => DuplicateKey,
        415 => UnsupportedMediaType,
        >= 500 => InternalError,
        _ => Error,
    };
}

/// <summary>
/// Collects field errors and throws one 400 <c>validation_failed</c> with all of them.
/// Field names are the JSON names (camelCase).
/// </summary>
public sealed class ApiValidator
{
    private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

    public ApiValidator Check(bool valid, string field, string message)
    {
        if (!valid)
        {
            Add(field, message);
        }
        return this;
    }

    public ApiValidator Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
        {
            errors[field] = list = [];
        }
        list.Add(message);
        return this;
    }

    public bool HasErrors => errors.Count > 0;

    public void ThrowIfInvalid()
    {
        if (errors.Count > 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "One or more fields are invalid.",
                errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}
