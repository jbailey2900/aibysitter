using Microsoft.AspNetCore.Http.Features;

namespace Aibysitter.Web.Infrastructure;

public static class RequestBody
{
    /// <summary>
    /// Body bytes, or null when over <paramref name="maxBytes"/>. Sets the server's body size limit for the request and
    /// stops reading past the cap, so a chunked body without Content-Length is never buffered beyond it.
    /// </summary>
    public static async Task<byte[]?> ReadCappedAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes + 1L;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        try
        {
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.ToArray();
    }
}
