using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Ai;
using VendorSure.Services.Ai;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Tests.Ai;

/// <summary>
/// Integration tests for the ai_usage repository. Talks to the dev DB.
/// Each test inserts its row(s) inside a <c>try/finally</c> that hard-deletes
/// them by id afterwards. The <c>ai_usage</c> table has no FK back to
/// <c>requests</c>/etc. for null cases, so inserts with null context keys
/// are isolated and safe to clean up.
/// </summary>
public sealed class AiUsageRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IAiUsageRepository _usage;
    private readonly IDbConnectionFactory _connectionFactory;

    public AiUsageRepositoryTests(InfrastructureTestFixture fixture)
    {
        _usage = fixture.ServiceProvider.GetRequiredService<IAiUsageRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task InsertAsync_returns_new_id_and_persists_all_fields()
    {
        var row = NewSuccessRow();
        int id = 0;
        try
        {
            id = await _usage.InsertAsync(row);
            Assert.True(id > 0);

            var fetched = await FetchByIdAsync(id);
            Assert.NotNull(fetched);
            Assert.Equal(row.Model, fetched!.Model);
            Assert.Equal(row.InputTokens, fetched.InputTokens);
            Assert.Equal(row.OutputTokens, fetched.OutputTokens);
            Assert.Equal(row.CostUsd, fetched.CostUsd);
            Assert.Equal(row.LatencyMs, fetched.LatencyMs);
            Assert.Equal("S", fetched.StatusChar);
            Assert.Equal(row.InputJson, fetched.InputJson);
            Assert.Equal(row.OutputJson, fetched.OutputJson);
            Assert.Null(fetched.ErrorText);
            // Context keys are all null on this row by NewSuccessRow.
            Assert.Null(fetched.RequestId);
            Assert.Null(fetched.WorkflowInstanceId);
            Assert.Null(fetched.WorkflowNodeId);
            Assert.Null(fetched.ValidationId);
            Assert.Null(fetched.PromptVersionId);
        }
        finally
        {
            if (id > 0) await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task InsertAsync_maps_Error_status_to_E()
    {
        var row = NewErrorRow(AiUsageStatus.Error);
        int id = 0;
        try
        {
            id = await _usage.InsertAsync(row);
            var fetched = await FetchByIdAsync(id);
            Assert.Equal("E", fetched!.StatusChar);
            Assert.Equal("boom", fetched.ErrorText);
            Assert.Null(fetched.OutputJson);
        }
        finally
        {
            if (id > 0) await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task InsertAsync_maps_Timeout_status_to_T()
    {
        var row = NewErrorRow(AiUsageStatus.Timeout);
        int id = 0;
        try
        {
            id = await _usage.InsertAsync(row);
            var fetched = await FetchByIdAsync(id);
            Assert.Equal("T", fetched!.StatusChar);
        }
        finally
        {
            if (id > 0) await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task InsertAsync_rejects_non_json_input_json()
    {
        // ai_usage.input_json has a CHECK constraint requiring valid JSON;
        // confirm the constraint actually fires when a buggy caller hands
        // something non-JSON.
        var bad = new AiUsage
        {
            CallTs = DateTime.UtcNow,
            Model = "claude-haiku-4-5",
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = 0m,
            LatencyMs = 0,
            Status = AiUsageStatus.Success,
            InputJson = "this is not json",
            OutputJson = null,
            ErrorText = null,
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _usage.InsertAsync(bad));
    }

    // ---------- helpers ----------

    private static AiUsage NewSuccessRow() => new()
    {
        CallTs = DateTime.UtcNow,
        Model = "claude-haiku-4-5",
        InputTokens = 10,
        OutputTokens = 20,
        CostUsd = 0.000110m,
        LatencyMs = 250,
        Status = AiUsageStatus.Success,
        InputJson = @"{""prompt"":""hi""}",
        OutputJson = @"{""content"":""hello""}",
        ErrorText = null,
    };

    private static AiUsage NewErrorRow(AiUsageStatus status) => new()
    {
        CallTs = DateTime.UtcNow,
        Model = "claude-haiku-4-5",
        InputTokens = 0,
        OutputTokens = 0,
        CostUsd = 0m,
        LatencyMs = 1500,
        Status = status,
        InputJson = @"{""prompt"":""hi""}",
        OutputJson = null,
        ErrorText = "boom",
    };

    private async Task<FetchedRow?> FetchByIdAsync(int id)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<FetchedRow>(@"
            SELECT
                id                  AS Id,
                request_id          AS RequestId,
                workflow_instance_id AS WorkflowInstanceId,
                workflow_node_id    AS WorkflowNodeId,
                validation_id       AS ValidationId,
                model               AS Model,
                prompt_version_id   AS PromptVersionId,
                input_tokens        AS InputTokens,
                output_tokens       AS OutputTokens,
                cost_usd            AS CostUsd,
                latency_ms          AS LatencyMs,
                CAST(status AS nvarchar(1)) AS StatusChar,
                input_json          AS InputJson,
                output_json         AS OutputJson,
                error_text          AS ErrorText
            FROM dbo.ai_usage WHERE id = @id;",
            new { id });
    }

    private async Task DeleteAsync(int id)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM dbo.ai_usage WHERE id = @id;", new { id });
    }

    private sealed class FetchedRow
    {
        public int Id { get; init; }
        public int? RequestId { get; init; }
        public int? WorkflowInstanceId { get; init; }
        public int? WorkflowNodeId { get; init; }
        public int? ValidationId { get; init; }
        public string Model { get; init; } = string.Empty;
        public int? PromptVersionId { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public decimal CostUsd { get; init; }
        public int LatencyMs { get; init; }
        public string StatusChar { get; init; } = string.Empty;
        public string InputJson { get; init; } = string.Empty;
        public string? OutputJson { get; init; }
        public string? ErrorText { get; init; }
    }
}
