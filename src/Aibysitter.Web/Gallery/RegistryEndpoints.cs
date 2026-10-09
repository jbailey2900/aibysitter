using Aibysitter.Rules;
using Microsoft.Net.Http.Headers;

namespace Aibysitter.Web.Gallery;

public static class RegistryEndpoints
{
    public const int SchemaVersion = 1;

    public static IEndpointRouteBuilder MapRegistry(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/registry.json", (HttpContext http, GalleryCatalog catalog, Infrastructure.SiteOptions site) =>
        {
            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=300";
            return Results.Json(new
            {
                schemaVersion = SchemaVersion,
                rulesetVersion = RulesetVersion.Current,
                entries = catalog.All.Select(e => new
                {
                    id = e.Id,
                    name = e.Name,
                    description = e.Description,
                    category = e.Category,
                    tags = e.Tags,
                    file = e.FileName,
                    format = e.FormatName,
                    installPath = e.InstallPath,
                    license = e.License,
                    lines = e.Lines.Count,
                    score = e.Score.Value,
                    grade = e.Score.Grade,
                    pageUrl = site.Url($"/Gallery/{e.Id}"),
                    downloadUrl = site.Url(e.DownloadPath),
                    badgeUrl = site.Url(e.BadgePath),
                }),
            });
        });

        endpoints.MapGet("/gallery/{id}/badge.svg", (string id, HttpContext http, GalleryCatalog catalog) =>
        {
            if (catalog.Find(id) is not { } entry)
            {
                return Results.NotFound();
            }

            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600";
            return Results.Text(ScoreBadge.Render(entry.Score), "image/svg+xml; charset=utf-8");
        });

        return endpoints;
    }
}
