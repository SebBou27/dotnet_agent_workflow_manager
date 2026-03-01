using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AgentWorkflowManager.Core;

public sealed class SqlServerQueryTool : IAgentTool
{
    private const int DefaultMaxRows = 200;
    private const int MaxRowsCap = 2000;

    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "commandType": { "type": "string", "enum": ["text", "storedProcedure"] },
        "sql": { "type": "string" },
        "procedure": { "type": "string" },
        "parameters": { "type": "object" },
        "maxRows": { "type": "integer", "minimum": 1, "maximum": 2000 }
      },
      "required": ["commandType"],
      "additionalProperties": false
    }
    """)!;

    private static readonly string[] ForbiddenSqlTokens =
    {
        "insert ", "update ", "delete ", "merge ", "alter ", "drop ", "truncate ", "create ", "exec ", "execute ", "grant ", "revoke ", "deny ", "backup ", "restore "
    };

    private readonly string _connectionString;
    private readonly HashSet<string> _storedProcedureAllowlist;
    private readonly int _commandTimeoutSeconds;

    public SqlServerQueryTool(string connectionString, IEnumerable<string>? storedProcedureAllowlist = null, int commandTimeoutSeconds = 30)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQL Server connection string cannot be empty.", nameof(connectionString));
        }

        _connectionString = connectionString;
        _storedProcedureAllowlist = new HashSet<string>(storedProcedureAllowlist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _commandTimeoutSeconds = commandTimeoutSeconds <= 0 ? 30 : commandTimeoutSeconds;

        Definition = new ToolDefinition(
            "db.sqlserver.query",
            "Exécute une requête SQL Server en lecture seule ou une procédure stockée autorisée.",
            ParametersSchema);
    }

    public string Name => Definition.Name;

    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var args = ParseArguments(context.ToolCall.Arguments);

        if (args.CommandType == CommandType.Text)
        {
            EnsureReadOnlySql(args.SqlOrProcedure);
        }
        else
        {
            EnsureStoredProcedureAllowed(args.SqlOrProcedure);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = _commandTimeoutSeconds;

        if (args.CommandType == CommandType.Text)
        {
            command.CommandType = CommandType.Text;
            command.CommandText = args.SqlOrProcedure;
        }
        else
        {
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = args.SqlOrProcedure;
        }

        foreach (var p in args.Parameters)
        {
            command.Parameters.AddWithValue("@" + p.Key.TrimStart('@'), p.Value ?? DBNull.Value);
        }

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= args.MaxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value;
            }

            rows.Add(row);
        }

        var payload = JsonSerializer.Serialize(new
        {
            commandType = args.CommandType == CommandType.Text ? "text" : "storedProcedure",
            statement = args.SqlOrProcedure,
            rowCount = rows.Count,
            truncated,
            rows,
        });

        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }

    private void EnsureStoredProcedureAllowed(string procedure)
    {
        if (_storedProcedureAllowlist.Count == 0)
        {
            return;
        }

        if (!_storedProcedureAllowlist.Contains(procedure))
        {
            throw new InvalidOperationException($"Stored procedure '{procedure}' is not in allowlist.");
        }
    }

    private static void EnsureReadOnlySql(string sql)
    {
        var normalized = " " + sql.Trim().ToLowerInvariant() + " ";

        if (!(normalized.TrimStart().StartsWith("select ", StringComparison.Ordinal)
              || normalized.TrimStart().StartsWith("with ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Only read-only SELECT/CTE SQL statements are allowed.");
        }

        if (ForbiddenSqlTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Forbidden SQL keyword detected for read-only mode.");
        }
    }

    private static ParsedArgs ParseArguments(JsonDocument doc)
    {
        var root = doc.RootElement;

        if (!root.TryGetProperty("commandType", out var commandTypeEl) || commandTypeEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("'commandType' is required and must be 'text' or 'storedProcedure'.");
        }

        var commandTypeRaw = commandTypeEl.GetString() ?? string.Empty;
        CommandType commandType = commandTypeRaw.Equals("storedProcedure", StringComparison.OrdinalIgnoreCase)
            ? CommandType.StoredProcedure
            : commandTypeRaw.Equals("text", StringComparison.OrdinalIgnoreCase)
                ? CommandType.Text
                : throw new InvalidOperationException("Unsupported commandType. Use 'text' or 'storedProcedure'.");

        string sqlOrProcedure;
        if (commandType == CommandType.Text)
        {
            if (!root.TryGetProperty("sql", out var sqlEl) || sqlEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sqlEl.GetString()))
            {
                throw new InvalidOperationException("'sql' string is required when commandType='text'.");
            }

            sqlOrProcedure = sqlEl.GetString()!;
        }
        else
        {
            if (!root.TryGetProperty("procedure", out var procEl) || procEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(procEl.GetString()))
            {
                throw new InvalidOperationException("'procedure' string is required when commandType='storedProcedure'.");
            }

            sqlOrProcedure = procEl.GetString()!;
        }

        var maxRows = DefaultMaxRows;
        if (root.TryGetProperty("maxRows", out var maxRowsEl) && maxRowsEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (maxRowsEl.ValueKind != JsonValueKind.Number || !maxRowsEl.TryGetInt32(out maxRows) || maxRows < 1)
            {
                throw new InvalidOperationException("'maxRows' must be an integer >= 1.");
            }
        }
        maxRows = Math.Min(maxRows, MaxRowsCap);

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in paramsEl.EnumerateObject())
            {
                parameters[prop.Name] = ConvertJsonValue(prop.Value);
            }
        }

        return new ParsedArgs(commandType, sqlOrProcedure, parameters, maxRows);
    }

    private static object? ConvertJsonValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? d : el.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => el.GetRawText(),
        };
    }

    private sealed record ParsedArgs(CommandType CommandType, string SqlOrProcedure, IReadOnlyDictionary<string, object?> Parameters, int MaxRows);
}
