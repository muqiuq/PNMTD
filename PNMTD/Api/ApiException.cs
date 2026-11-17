namespace PNMTD.Api;

/// <summary>
/// A v1 error with its HTTP status and the stable <c>code</c> of the ProblemDetails body
/// (docs/webui-migration-plan.md §3.7). Thrown by the domain services, written by <see cref="ApiExceptionFilter"/>.
/// <see cref="Extensions"/> become members of the body next to <c>code</c>, such as an edit conflict's revision.
/// </summary>
public sealed class ApiException(int status, string code, string detail, IDictionary<string, string[]>? errors = null,
    IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(detail)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, $"{what} not found.");

    public static ApiException Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, code, detail);

    /// <summary>
    /// 409 <c>edit_conflict</c> (docs/locations-knowledge-plan.md §5.7): someone saved since the revision the save started
    /// from. The body names their <c>revision</c>, <c>by</c> and <c>at</c>, so the editor can show both sides.
    /// </summary>
    public static ApiException EditConflict(int revision, string? by, DateTime? at, string detail) =>
        new(StatusCodes.Status409Conflict, ApiErrorCodes.EditConflict, detail,
            extensions: new Dictionary<string, object?> { ["revision"] = revision, ["by"] = by, ["at"] = at });

    /// <summary>403 <c>forbidden_role</c>: a right the caller lacks, where an attribute can't say it (one's own column, say).</summary>
    public static ApiException Forbidden(string detail) => new(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole, detail);

    /// <summary>
    /// 403 <c>missing_right</c> with the area and level, as the authorization handler writes it, for a right only a service can
    /// tell: a certificate sensor's writes need Certificates: Edit besides the endpoint's Monitoring: Edit.
    /// </summary>
    public static ApiException MissingRight(Auth.Rights.Area area, Auth.Rights.Level level) =>
        new(StatusCodes.Status403Forbidden, "missing_right", $"You need {Auth.Rights.RightsCatalog.Of(area).Label}: {level}.",
            extensions: new Dictionary<string, object?> { ["area"] = area.ToString(), ["level"] = level.ToString() });

    /// <summary>400 <c>validation_failed</c> without a field.</summary>
    public static ApiException BadRequest(string detail) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, detail);

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

    /// <summary>A save that started from an older revision (docs/locations-knowledge-plan.md §5.7).</summary>
    public const string EditConflict = "edit_conflict";
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
