using System.Text.Json;

namespace NetDocsImporter.Core;

public enum ProfileFieldMode
{
    Label,
    Code
}

public sealed class ProfileFieldEntry
{
    public ProfileFieldEntry(string field, string value, ProfileFieldMode mode)
    {
        Field = field;
        Value = value;
        Mode = mode;
    }

    public string Field { get; }

    public string Value { get; }

    public ProfileFieldMode Mode { get; }
}

public static class ProfilePayloadCodec
{
    public static IReadOnlyList<ProfileFieldEntry> Deserialize(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Array.Empty<ProfileFieldEntry>();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var entries = new List<ProfileFieldEntry>();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    entries.Add(new ProfileFieldEntry(property.Name, property.Value.GetString() ?? string.Empty, ProfileFieldMode.Label));
                }

                return entries;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var entries = new List<ProfileFieldEntry>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var field = ReadString(element, "field");
                    var value = ReadString(element, "value");
                    var mode = ReadString(element, "mode");
                    var parsedMode = string.Equals(mode, "code", StringComparison.OrdinalIgnoreCase) ? ProfileFieldMode.Code : ProfileFieldMode.Label;
                    if (!string.IsNullOrWhiteSpace(field))
                    {
                        entries.Add(new ProfileFieldEntry(field, value, parsedMode));
                    }
                }

                return entries;
            }
        }
        catch
        {
        }

        return Array.Empty<ProfileFieldEntry>();
    }

    public static string? Serialize(IEnumerable<ProfileFieldEntry> entries)
    {
        var list = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Field))
            .Select(e => new ProfileFieldPayload
            {
                Field = e.Field,
                Value = e.Value,
                Mode = e.Mode == ProfileFieldMode.Code ? "code" : "label"
            })
            .ToList();

        if (list.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            var alternate = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
            if (!element.TryGetProperty(alternate, out property))
            {
                return string.Empty;
            }
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private sealed class ProfileFieldPayload
    {
        public string Field { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public string Mode { get; set; } = "label";
    }
}
