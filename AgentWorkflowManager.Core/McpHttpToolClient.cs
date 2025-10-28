using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentWorkflowManager.Core;

/// <summary>
/// IMcpToolClient implementation backed by the official MCP HTTP transport.
/// </summary>
public sealed class McpHttpToolClient : IMcpToolClient, IAsyncDisposable
{
    private static readonly Regex EnvPattern = new(@"\$\{ENV:([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments = new Dictionary<string, object?>(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DotEnvVariables = new(LoadDotEnv, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, Lazy<Task<ClientHandle>>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly McpClientOptions _clientOptions;
    private readonly ILoggerFactory _loggerFactory;

    public McpHttpToolClient(string? clientName = null, string? clientVersion = null)
        : this(clientName, clientVersion, NullLoggerFactory.Instance)
    {
    }

    public McpHttpToolClient(string? clientName, string? clientVersion, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        var assemblyVersion = clientVersion ?? typeof(McpHttpToolClient).Assembly.GetName().Version?.ToString() ?? "0.1.0";
        var name = string.IsNullOrWhiteSpace(clientName) ? "AgentWorkflowManager" : clientName!;

        _clientOptions = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = name, Version = assemblyVersion },
            ProtocolVersion = null,
        };
    }

    public async Task<string> InvokeAsync(McpToolDescriptor descriptor, JsonDocument arguments, CancellationToken cancellationToken)
    {
        var client = await GetOrCreateClientAsync(descriptor, cancellationToken).ConfigureAwait(false);
        var toolName = descriptor.Command ?? descriptor.Name;
        var argumentDictionary = CreateArgumentDictionary(arguments);
        var result = await client.CallToolAsync(toolName, argumentDictionary, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    private Task<McpClient> GetOrCreateClientAsync(McpToolDescriptor descriptor, CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(descriptor.Server)
            ? descriptor.Server!
            : descriptor.Endpoint ?? descriptor.Name;

        var lazy = _clients.GetOrAdd(key, _ => new Lazy<Task<ClientHandle>>(() => CreateClientAsync(descriptor), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.ContinueWith(static t => t.Result.Client, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<ClientHandle> CreateClientAsync(McpToolDescriptor descriptor)
    {
        var endpointUri = BuildEndpointUri(descriptor);
        var headers = BuildHeaders(descriptor.Headers);

        var httpOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
            TransportMode = HttpTransportMode.AutoDetect,
            Name = descriptor.Server ?? endpointUri.Host,
            AdditionalHeaders = headers,
        };

        var transport = new HttpClientTransport(httpOptions);
        var client = await McpClient.CreateAsync(transport, _clientOptions, _loggerFactory).ConfigureAwait(false);
        return new ClientHandle(client, transport);
    }

    private static Uri BuildEndpointUri(McpToolDescriptor descriptor)
    {
        var endpointValue = ExpandPlaceholders(descriptor.Endpoint);
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            throw new InvalidOperationException($"MCP tool '{descriptor.Name}' is missing an endpoint.");
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"MCP tool '{descriptor.Name}' endpoint '{endpointValue}' is not a valid absolute URI.");
        }

        if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.Port == 80 ? -1 : uri.Port }.Uri;
        }
        else if (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = uri.Port == 80 ? -1 : uri.Port }.Uri;
        }

        return uri;
    }

    private static IDictionary<string, string>? BuildHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            resolved[pair.Key] = ExpandPlaceholders(pair.Value);
        }

        return resolved;
    }

    private static string ExpandPlaceholders(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        return EnvPattern.Replace(value, static match =>
        {
            var envName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(envName) ?? TryGetDotEnvValue(envName) ?? string.Empty;
        });
    }

    private static IReadOnlyDictionary<string, object?> CreateArgumentDictionary(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return EmptyArguments;
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var boxedValue = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), SerializerOptions);
            dict[property.Name] = boxedValue;
        }

        return dict;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _clients.Values)
        {
            if (!entry.IsValueCreated)
            {
                continue;
            }

            var handle = await entry.Value.ConfigureAwait(false);
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? TryGetDotEnvValue(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return DotEnvVariables.Value.TryGetValue(name, out var value) ? value : null;
    }

    private static IReadOnlyDictionary<string, string> LoadDotEnv()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate))
                {
                    foreach (var line in File.ReadAllLines(candidate))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var index = trimmed.IndexOf('=');
                        if (index <= 0)
                        {
                            continue;
                        }

                        var key = trimmed[..index].Trim();
                        if (key.Length == 0)
                        {
                            continue;
                        }

                        var value = trimmed[(index + 1)..].Trim().Trim('"');
                        dict[key] = value;
                    }

                    break;
                }

                directory = directory.Parent;
            }
        }
        catch
        {
        }

        return dict;
    }

    private sealed class ClientHandle : IAsyncDisposable
    {
        public ClientHandle(McpClient client, IAsyncDisposable transport)
        {
            Client = client;
            _transport = transport;
        }

        public McpClient Client { get; }

        private readonly IAsyncDisposable _transport;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
