using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AgentWorkflowManager.Core;

public sealed class SqlServerSchemaTablesTool : IAgentTool
{
    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "schema": { "type": "string" },
        "table": { "type": "string" },
        "maxRows": { "type": "integer", "minimum": 1, "maximum": 5000 }
      },
      "additionalProperties": false
    }
    """)!;

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;

    public SqlServerSchemaTablesTool(string connectionString, int commandTimeoutSeconds = 30)
    {
        _connectionString = connectionString;
        _commandTimeoutSeconds = commandTimeoutSeconds <= 0 ? 30 : commandTimeoutSeconds;
        Definition = new ToolDefinition("db.sqlserver.schema_tables", "Liste tables/colonnes SQL Server (metadata read-only).", ParametersSchema);
    }

    public string Name => Definition.Name;
    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var root = context.ToolCall.Arguments.RootElement;
        var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var table = root.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var maxRows = root.TryGetProperty("maxRows", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mr) && mr > 0 ? Math.Min(mr, 5000) : 1000;

        var sql = @"
SELECT TOP (@maxRows)
  c.TABLE_SCHEMA,
  c.TABLE_NAME,
  c.COLUMN_NAME,
  c.DATA_TYPE,
  c.IS_NULLABLE,
  c.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE (@schema IS NULL OR c.TABLE_SCHEMA = @schema)
  AND (@table IS NULL OR c.TABLE_NAME = @table)
ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@maxRows", maxRows);
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@table", (object?)table ?? DBNull.Value);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        var payload = JsonSerializer.Serialize(new { schema, table, count = rows.Count, rows });
        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }
}

public sealed class SqlServerSchemaProceduresTool : IAgentTool
{
    private static readonly JsonNode ParametersSchema = JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "schema": { "type": "string" },
        "procedure": { "type": "string" },
        "includeParameters": { "type": "boolean" }
      },
      "additionalProperties": false
    }
    """)!;

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;

    public SqlServerSchemaProceduresTool(string connectionString, int commandTimeoutSeconds = 30)
    {
        _connectionString = connectionString;
        _commandTimeoutSeconds = commandTimeoutSeconds <= 0 ? 30 : commandTimeoutSeconds;
        Definition = new ToolDefinition("db.sqlserver.schema_procedures", "Liste procédures stockées SQL Server (+ paramètres optionnels).", ParametersSchema);
    }

    public string Name => Definition.Name;
    public ToolDefinition Definition { get; }

    public async Task<AgentToolExecutionResult> InvokeAsync(ToolInvocationContext context, CancellationToken cancellationToken)
    {
        var root = context.ToolCall.Arguments.RootElement;
        var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var procedure = root.TryGetProperty("procedure", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var includeParameters = root.TryGetProperty("includeParameters", out var ip) && ip.ValueKind is JsonValueKind.True or JsonValueKind.False && ip.GetBoolean();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var procedures = new List<Dictionary<string, object?>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = _commandTimeoutSeconds;
            cmd.CommandText = @"
SELECT p.SPECIFIC_SCHEMA, p.SPECIFIC_NAME
FROM INFORMATION_SCHEMA.ROUTINES p
WHERE p.ROUTINE_TYPE = 'PROCEDURE'
  AND (@schema IS NULL OR p.SPECIFIC_SCHEMA = @schema)
  AND (@procedure IS NULL OR p.SPECIFIC_NAME = @procedure)
ORDER BY p.SPECIFIC_SCHEMA, p.SPECIFIC_NAME;";
            cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@procedure", (object?)procedure ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                procedures.Add(new Dictionary<string, object?>
                {
                    ["schema"] = reader.GetString(0),
                    ["procedure"] = reader.GetString(1),
                });
            }
        }

        List<Dictionary<string, object?>>? parameters = null;
        if (includeParameters)
        {
            parameters = new List<Dictionary<string, object?>>();
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandTimeout = _commandTimeoutSeconds;
            cmd2.CommandText = @"
SELECT prm.SPECIFIC_SCHEMA, prm.SPECIFIC_NAME, prm.PARAMETER_NAME, prm.DATA_TYPE, prm.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.PARAMETERS prm
WHERE (@schema IS NULL OR prm.SPECIFIC_SCHEMA = @schema)
  AND (@procedure IS NULL OR prm.SPECIFIC_NAME = @procedure)
ORDER BY prm.SPECIFIC_SCHEMA, prm.SPECIFIC_NAME, prm.ORDINAL_POSITION;";
            cmd2.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
            cmd2.Parameters.AddWithValue("@procedure", (object?)procedure ?? DBNull.Value);

            await using var r2 = await cmd2.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r2.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                parameters.Add(new Dictionary<string, object?>
                {
                    ["schema"] = r2.GetString(0),
                    ["procedure"] = r2.GetString(1),
                    ["parameter"] = await r2.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : r2.GetString(2),
                    ["dataType"] = await r2.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : r2.GetString(3),
                    ["ordinal"] = await r2.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : r2.GetValue(4),
                });
            }
        }

        var payload = JsonSerializer.Serialize(new { schema, procedure, proceduresCount = procedures.Count, procedures, parametersCount = parameters?.Count ?? 0, parameters });
        return new AgentToolExecutionResult(context.ToolCall.CallId, payload);
    }
}
