namespace VendorSure.Infrastructure.Ai;

/// <summary>
/// Configuration bound from the <c>Anthropic</c> section of
/// <c>appsettings.json</c> / user secrets / environment variables.
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> is the only required value. In dev, set it via
/// <c>dotnet user-secrets set Anthropic:ApiKey sk-ant-...</c>; in
/// production, environment variable <c>Anthropic__ApiKey</c> or a
/// secret-store binding.
///
/// The <em>model name</em> is intentionally NOT here — it lives in the
/// <c>AI.Model</c> setting so admins can change it without a redeploy.
/// </remarks>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string? ApiKey { get; init; }
}
