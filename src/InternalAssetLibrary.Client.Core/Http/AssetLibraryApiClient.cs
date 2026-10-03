using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Client.Core.Http;

public interface IAccessTokenProvider
{
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class StaticAccessTokenProvider : IAccessTokenProvider
{
    private string? _accessToken;

    public StaticAccessTokenProvider(string? accessToken = null)
    {
        _accessToken = accessToken;
    }

    public string? AccessToken
    {
        get => Volatile.Read(ref _accessToken);
        set => Volatile.Write(ref _accessToken, value);
    }

    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AccessToken);
    }
}

public sealed partial class AssetLibraryApiClient
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly IAccessTokenProvider? _accessTokenProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _clientVersion;

    public AssetLibraryApiClient(
        HttpClient httpClient,
        Uri baseAddress,
        IAccessTokenProvider? accessTokenProvider = null,
        JsonSerializerOptions? jsonOptions = null,
        string? clientVersion = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseAddress = ValidateBaseAddress(baseAddress);
        _accessTokenProvider = accessTokenProvider;
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
        _clientVersion = NormalizeClientVersion(clientVersion);
    }

    public Uri BaseAddress => _baseAddress;

    public Task<TResponse> GetAsync<TResponse>(
        string relativeUri,
        CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(HttpMethod.Get, relativeUri, null, cancellationToken);

    public Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativeUri,
        TRequest body,
        CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(HttpMethod.Post, relativeUri, body, cancellationToken);

    public Task<TResponse> PutAsync<TRequest, TResponse>(
        string relativeUri,
        TRequest body,
        CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(HttpMethod.Put, relativeUri, body, cancellationToken);

    public Task<TResponse> PatchAsync<TRequest, TResponse>(
        string relativeUri,
        TRequest body,
        CancellationToken cancellationToken = default) =>
        SendAsync<TResponse>(HttpMethod.Patch, relativeUri, body, cancellationToken);

    public Task DeleteAsync(string relativeUri, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, relativeUri, null, cancellationToken);

    public Task PostAsync<TRequest>(
        string relativeUri,
        TRequest body,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, relativeUri, body, cancellationToken);

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string relativeUri,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(method, relativeUri, body, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            throw new InvalidApiResponseException("The API returned no JSON response body.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var value = await JsonSerializer.DeserializeAsync<TResponse>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);
            return value ?? throw new InvalidApiResponseException(
                "The API returned a JSON null response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidApiResponseException(
                "The API returned an invalid JSON response.",
                exception);
        }
    }

    public async Task SendAsync(
        HttpMethod method,
        string relativeUri,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(method, relativeUri, body, cancellationToken)
            .ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var content = body is null
            ? null
            : JsonContent.Create(body, body.GetType(), options: _jsonOptions);
        return await SendCoreAsync(
            method,
            relativeUri,
            content,
            "application/json",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string relativeUri,
        HttpContent? content,
        string accept,
        CancellationToken cancellationToken,
        RangeHeaderValue? range = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        var requestUri = ResolveRequestUri(relativeUri);
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = content
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Range = range;
        if (_clientVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("X-IAL-Client-Version", _clientVersion);
        }

        if (_accessTokenProvider is not null)
        {
            var accessToken = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ThrowIfFailedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ApiProblem? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize<ApiProblem>(content, _jsonOptions);
        }
        catch (JsonException)
        {
            // Non-JSON proxy and web-server errors are represented by the text fallback below.
        }

        var detail = problem?.Detail;
        if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(content))
        {
            detail = content.Length <= 2048 ? content : content[..2048];
        }

        throw new AssetLibraryApiException(
            response.StatusCode,
            problem?.Title ?? response.ReasonPhrase ?? "API request failed",
            detail,
            problem?.Code,
            problem?.TraceId,
            problem?.Errors);
    }

    private Uri ResolveRequestUri(string relativeUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUri);
        if (Uri.TryCreate(relativeUri, UriKind.Absolute, out _))
        {
            throw new ArgumentException("API request addresses must be relative.", nameof(relativeUri));
        }

        var requestUri = new Uri(_baseAddress, relativeUri);
        if (!string.Equals(requestUri.Scheme, _baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != _baseAddress.Port)
        {
            throw new ArgumentException("The API request address points outside the configured server.", nameof(relativeUri));
        }

        return requestUri;
    }

    private static Uri ValidateBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri ||
            (baseAddress.Scheme != Uri.UriSchemeHttps && baseAddress.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The API base address must be an absolute HTTP(S) URI.", nameof(baseAddress));
        }

        var value = baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/", UriKind.Absolute);
        return value;
    }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string? NormalizeClientVersion(string? clientVersion)
    {
        var normalized = clientVersion?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > 128 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
        {
            throw new ArgumentException("The client version contains invalid characters.", nameof(clientVersion));
        }

        return normalized;
    }

    private sealed record ApiProblem(
        string? Title,
        string? Detail,
        string? Code,
        string? TraceId,
        IReadOnlyDictionary<string, string[]>? Errors);
}

public sealed class AssetLibraryApiException : HttpRequestException
{
    public AssetLibraryApiException(
        HttpStatusCode statusCode,
        string title,
        string? detail,
        string? code,
        string? traceId,
        IReadOnlyDictionary<string, string[]>? validationErrors)
        : base(detail is null ? title : $"{title}: {detail}", null, statusCode)
    {
        Title = title;
        Detail = detail;
        Code = code;
        TraceId = traceId;
        ValidationErrors = validationErrors;
    }

    public string Title { get; }

    public string? Detail { get; }

    public string? Code { get; }

    public string? TraceId { get; }

    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; }
}

public sealed class InvalidApiResponseException : HttpRequestException
{
    public InvalidApiResponseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
