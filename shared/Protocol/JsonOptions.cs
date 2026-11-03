using System.Text.Json;

namespace YfgDesk.Shared.Protocol;

/// <summary>
/// JSON options shared between server and client. camelCase, case-insensitive.
/// </summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
