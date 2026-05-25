using Dapper;
using VendorSure.Domain.Ai;
using VendorSure.Services.Ai;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Ai;

internal sealed class AiUsageRepository : IAiUsageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AiUsageRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> InsertAsync(AiUsage row, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO dbo.ai_usage
                (request_id, workflow_instance_id, workflow_node_id, validation_id,
                 call_ts, model, prompt_version_id,
                 input_tokens, output_tokens, cost_usd, latency_ms, status,
                 input_json, output_json, error_text)
            OUTPUT INSERTED.id
            VALUES
                (@RequestId, @WorkflowInstanceId, @WorkflowNodeId, @ValidationId,
                 @CallTs, @Model, @PromptVersionId,
                 @InputTokens, @OutputTokens, @CostUsd, @LatencyMs, @Status,
                 @InputJson, @OutputJson, @ErrorText);";

        var parameters = new
        {
            row.RequestId,
            row.WorkflowInstanceId,
            row.WorkflowNodeId,
            row.ValidationId,
            row.CallTs,
            row.Model,
            row.PromptVersionId,
            row.InputTokens,
            row.OutputTokens,
            row.CostUsd,
            row.LatencyMs,
            Status = StatusToChar(row.Status),
            row.InputJson,
            row.OutputJson,
            row.ErrorText,
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        return await connection.ExecuteScalarAsync<int>(command);
    }

    private static string StatusToChar(AiUsageStatus status) => status switch
    {
        AiUsageStatus.Success => "S",
        AiUsageStatus.Error => "E",
        AiUsageStatus.Timeout => "T",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown AiUsageStatus."),
    };
}
