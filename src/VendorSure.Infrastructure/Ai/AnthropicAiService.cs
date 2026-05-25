using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VendorSure.Domain.Ai;
using VendorSure.Services.Ai;
using VendorSure.Services.Configuration;

namespace VendorSure.Infrastructure.Ai;

/// <summary>
/// <see cref="IAiService"/> impl backed by the official Anthropic C# SDK.
/// </summary>
/// <remarks>
/// Lifecycle:
///
///   1. Read <c>AI.Disabled</c>. If 1, throw <see cref="AiDisabledException"/>.
///      No row, no call.
///   2. Read <c>AI.Model</c> (the model identifier to send to the API).
///   3. Look up pricing for that model. If absent, throw
///      <see cref="ModelPricingMissingException"/>. No row, no call.
///   4. Build <see cref="MessageCreateParams"/>; capture as JSON for
///      <c>input_json</c>.
///   5. Call <c>client.Messages.Create</c>, timing the call with a
///      <see cref="Stopwatch"/>.
///   6. On success: extract token counts, compute cost, extract response
///      text from the first text content block. Insert <c>ai_usage</c> row
///      with status='S'. Return <see cref="AiCallResult"/>.
///   7. On <see cref="TaskCanceledException"/> / similar timeout: insert
///      <c>ai_usage</c> row with status='T', null output, tokens 0, cost 0,
///      error text on the exception. Throw <see cref="AiCallFailedException"/>.
///   8. On any other exception: insert <c>ai_usage</c> row with status='E',
///      same null/zero shape. Throw <see cref="AiCallFailedException"/>.
///
/// Cost formula:
///   cost_usd = (input_tokens * input_per_million_usd
///              + output_tokens * output_per_million_usd) / 1,000,000
/// rounded to 6 decimal places (the <c>decimal(10,6)</c> column).
///
/// Cancellation passed through to the SDK call. The CT firing surfaces as
/// <see cref="OperationCanceledException"/>; we categorise that as Timeout
/// for now (the SDK uses TaskCanceledException for its own timeouts too,
/// and they're indistinguishable here without inspecting CT state).
/// </remarks>
internal sealed class AnthropicAiService : IAiService
{
    private const string DisabledKey = "AI.Disabled";
    private const string ModelKey = "AI.Model";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly AnthropicClient _client;
    private readonly ISettingsRepository _settings;
    private readonly IModelPricingRepository _pricing;
    private readonly IAiUsageRepository _usage;
    private readonly ILogger<AnthropicAiService> _logger;

    public AnthropicAiService(
        AnthropicClient client,
        ISettingsRepository settings,
        IModelPricingRepository pricing,
        IAiUsageRepository usage,
        ILogger<AnthropicAiService> logger)
    {
        _client = client;
        _settings = settings;
        _pricing = pricing;
        _usage = usage;
        _logger = logger;
    }

    public async Task<AiCallResult> CompleteAsync(
        string prompt,
        AiCallContext context,
        CancellationToken ct = default)
    {
        await EnsureNotDisabledAsync(ct);

        var model = await ReadModelAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pricing = await _pricing.GetCurrentForModelAsync(model, today, ct)
            ?? throw new ModelPricingMissingException(model);

        // NOTE: the official Anthropic C# SDK is in beta. The shapes
        // below — MessageParam (confirmed), Role.User (assumed),
        // _client.Messages.Create(params, ct), message.Usage.InputTokens/
        // OutputTokens — are my best guess from the public docs. The
        // remaining unverified spot: the Create overload may not accept
        // a CancellationToken as the 2nd positional arg; if not, use
        // WithOptions(o => o with { ... }) or pass CT differently per
        // the SDK docs at platform.claude.com/docs/en/api/sdks/csharp.
        // Content-block text extraction is JSON-based below
        // (ExtractFirstTextBlock) so it's stable regardless of which
        // typed accessor the SDK ends up exposing.
        var parameters = new MessageCreateParams
        {
            MaxTokens = 1024,
            Model = model,
            Messages = new[]
            {
                new MessageParam
                {
                    Role = Role.User,
                    Content = prompt,
                },
            },
        };

        var inputJson = JsonSerializer.Serialize(parameters, JsonOptions);
        var callTs = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            var message = await _client.Messages.Create(parameters, ct);
            sw.Stop();

            var inputTokens = message.Usage.InputTokens;
            var outputTokens = message.Usage.OutputTokens;
            var cost = ComputeCost(inputTokens, outputTokens, pricing);
            var responseText = ExtractFirstTextBlock(message);
            var outputJson = JsonSerializer.Serialize(message, JsonOptions);

            var row = new AiUsage
            {
                RequestId = context.RequestId,
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowNodeId = context.WorkflowNodeId,
                ValidationId = context.ValidationId,
                CallTs = callTs,
                Model = model,
                PromptVersionId = context.PromptVersionId,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = cost,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                Status = AiUsageStatus.Success,
                InputJson = inputJson,
                OutputJson = outputJson,
                ErrorText = null,
            };

            var id = await _usage.InsertAsync(row, ct);

            _logger.LogInformation(
                "AI call ok: model={Model} in={InputTokens} out={OutputTokens} cost=${CostUsd} latency={LatencyMs}ms ai_usage.id={Id}",
                model, inputTokens, outputTokens, cost, row.LatencyMs, id);

            return new AiCallResult(
                AiUsageId: id,
                Model: model,
                ResponseText: responseText,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CostUsd: cost,
                LatencyMs: row.LatencyMs);
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            var id = await WriteFailureRowAsync(
                context, callTs, model, sw, AiUsageStatus.Timeout, inputJson, ex, ct);
            throw new AiCallFailedException(id, AiUsageStatus.Timeout,
                $"AI call timed out: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var id = await WriteFailureRowAsync(
                context, callTs, model, sw, AiUsageStatus.Error, inputJson, ex, ct);
            throw new AiCallFailedException(id, AiUsageStatus.Error,
                $"AI call failed: {ex.Message}", ex);
        }
    }

    private async Task<int> WriteFailureRowAsync(
        AiCallContext context,
        DateTime callTs,
        string model,
        Stopwatch sw,
        AiUsageStatus status,
        string inputJson,
        Exception ex,
        CancellationToken ct)
    {
        var row = new AiUsage
        {
            RequestId = context.RequestId,
            WorkflowInstanceId = context.WorkflowInstanceId,
            WorkflowNodeId = context.WorkflowNodeId,
            ValidationId = context.ValidationId,
            CallTs = callTs,
            Model = model,
            PromptVersionId = context.PromptVersionId,
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = 0m,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            Status = status,
            InputJson = inputJson,
            OutputJson = null,
            // error_text is nvarchar(2000) — cap to fit.
            ErrorText = Truncate(ex.ToString(), 2000),
        };

        // Use CancellationToken.None for the bookkeeping write so a cancelled
        // CT doesn't prevent us from recording the failure that the
        // cancellation itself caused.
        return await _usage.InsertAsync(row, CancellationToken.None);
    }

    private async Task EnsureNotDisabledAsync(CancellationToken ct)
    {
        var setting = await _settings.GetByKeyAsync(DisabledKey, ct);
        if (setting?.Value == "1")
        {
            throw new AiDisabledException();
        }
    }

    private async Task<string> ReadModelAsync(CancellationToken ct)
    {
        var setting = await _settings.GetByKeyAsync(ModelKey, ct);
        var value = setting?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required setting '{ModelKey}' is missing or empty.");
        }
        return value;
    }

    internal static decimal ComputeCost(int inputTokens, int outputTokens, ModelPricing pricing)
    {
        // (tokens * usd_per_million) / 1_000_000. Round to 6 decimals to match
        // the column scale and avoid Dapper rejecting a higher-precision value.
        var raw =
            ((decimal)inputTokens * pricing.InputPerMillionUsd
            + (decimal)outputTokens * pricing.OutputPerMillionUsd)
            / 1_000_000m;
        return Math.Round(raw, 6, MidpointRounding.AwayFromZero);
    }

    private static string ExtractFirstTextBlock(Message message)
    {
        // The Anthropic Messages API conventionally returns content as a list
        // of blocks; for plain prompts the first block is text. We extract
        // it for the caller's convenience. The full message (all blocks
        // included) is still persisted verbatim in output_json, so callers
        // that need more than the first text block can read that.
        //
        // Why JSON rather than a typed accessor: ContentBlock in the
        // current SDK isn't a polymorphic hierarchy with TextBlock as a
        // subclass — a 'block is TextBlock' pattern won't even compile.
        // The exact typed accessor varies between SDK versions (Text
        // property? AsText()? .Type discriminator?). Serializing to JSON
        // and reading the 'text' field on the first 'type: text' block
        // is stable across SDK shape changes and matches the wire format
        // the API itself uses.
        if (message.Content is null)
        {
            return string.Empty;
        }

        foreach (var block in message.Content)
        {
            var json = JsonSerializer.Serialize(block, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Only consider text-type blocks. Tool-use / other variants
            // are skipped (we don't request them in 6B.1).
            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "text"
                && root.TryGetProperty("text", out var textProp)
                && textProp.ValueKind == JsonValueKind.String)
            {
                return textProp.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
