using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace AgentWorkflowManager.Core;

internal interface IOpenAiResponseClient
{
    Task<OpenAiResponseEnvelope> CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal wrapper around the OpenAI Responses API.
/// </summary>
public sealed class OpenAiResponseClient : IOpenAiResponseClient, IDisposable
{
    private static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _apiKey;

    public OpenAiResponseClient(OpenAiOptions options, HttpClient? httpClient = null, JsonSerializerOptions? serializerOptions = null)
    {
        _apiKey = options.ApiKey;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    async Task<OpenAiResponseEnvelope> IOpenAiResponseClient.CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, _serializerOptions), Encoding.UTF8, "application/json"),
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=v2");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"OpenAI API returned {(int)response.StatusCode} {response.ReasonPhrase}: {payload}");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync<OpenAiResponseEnvelope>(responseStream, _serializerOptions, cancellationToken).ConfigureAwait(false);

        return envelope ?? new OpenAiResponseEnvelope();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
