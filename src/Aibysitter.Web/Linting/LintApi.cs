using System.Text.Json;
using Aibysitter.Rules;

namespace Aibysitter.Web.Linting;

/// <summary>POST /api/lint: JSON in, findings and score out. No auth, no CORS, nothing stored.</summary>
public static class LintApi
{
    public const string Path = "/api/lint";
    public const int MaxBodyBytes = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Request(string? Content, string? Format, IReadOnlyList<string?>? Disable);

    public static IEndpointRouteBuilder MapLintApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, HandleAsync);
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, LintService lint, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType())
        {
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Content-Type must be application/json.");
        }

        var body = await Infrastructure.RequestBody.ReadCappedAsync(context.Request, MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"Request body is limited to {MaxBodyBytes / 1024} KB.");
        }

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(body, Json);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request body is not valid JSON.");
        }

        if (Validate(request, lint, out var format, out var disabled) is { } errors)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            return Results.Json(lint.Lint(request!.Content!, format, disabled, "api").Report, Json);
        }
        catch (LintTimeoutException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: ex.Message);
        }
    }

    private static Dictionary<string, string[]>? Validate(Request? request, LintService lint, out RulesFormat format, out IReadOnlyList<string> disabled)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        format = RulesFormat.Auto;
        disabled = [];

        if (string.IsNullOrWhiteSpace(request?.Content))
        {
            errors["content"] = ["Required."];
        }
        else if (request.Content.Length > LintLimits.MaxContentLength)
        {
            errors["content"] = [$"Limited to {LintLimits.MaxContentLength:N0} characters."];
        }

        if (request?.Format is { } name && !RulesFormats.TryParse(name, out format))
        {
            errors["format"] = [$"Unknown format \"{name}\". Use one of: {string.Join(", ", Enum.GetNames<RulesFormat>())}."];
        }

        if (!lint.TryNormalizeDisabled(request?.Disable, out disabled, out var unknown))
        {
            errors["disable"] = [$"Not a lint rule ID: {string.Join(", ", unknown)}."];
        }

        return errors.Count == 0 ? null : errors;
    }
}
