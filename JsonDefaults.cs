using System.Text.Json;

namespace FreightCopilot;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
