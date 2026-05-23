namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// Lifecycle state of a <see cref="RequestTypeVersion"/>. Maps to the
/// <c>request_state</c> char(1) column on <c>request_type_versions</c>
/// via <see cref="RequestStateCodes"/>.
/// </summary>
public enum RequestState
{
    /// <summary>Editable. No live requests bound to it. ('D')</summary>
    Draft,

    /// <summary>Immutable. New requests bind to this version. ('I')</summary>
    InService,

    /// <summary>
    /// A newer version of the same Request Type is now In Service. ('S')
    /// In-flight requests already bound to this version continue on it.
    /// </summary>
    Superseded,
}

/// <summary>
/// String codes the database stores for <see cref="RequestState"/>. The
/// <c>CK_request_type_versions_state</c> CHECK constraint enforces these
/// exact characters.
/// </summary>
public static class RequestStateCodes
{
    public const string Draft = "D";
    public const string InService = "I";
    public const string Superseded = "S";
}

public static class RequestStateExtensions
{
    public static string ToCode(this RequestState state) => state switch
    {
        RequestState.Draft      => RequestStateCodes.Draft,
        RequestState.InService  => RequestStateCodes.InService,
        RequestState.Superseded => RequestStateCodes.Superseded,
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Unknown RequestState value."),
    };

    /// <summary>
    /// Parses the char-code form the database stores. Throws on anything
    /// other than 'D'/'I'/'S' — if SQL gives us something else, the schema's
    /// CHECK constraint has been bypassed and that's a real error.
    /// </summary>
    public static RequestState FromCode(string code) => code switch
    {
        RequestStateCodes.Draft      => RequestState.Draft,
        RequestStateCodes.InService  => RequestState.InService,
        RequestStateCodes.Superseded => RequestState.Superseded,
        _ => throw new ArgumentOutOfRangeException(
            nameof(code), code, "Unknown RequestState code from DB."),
    };
}
