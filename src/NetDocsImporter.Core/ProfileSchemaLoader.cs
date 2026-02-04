using System.Text.Json;

namespace NetDocsImporter.Core;

public static class ProfileSchemaLoader
{
    public static async Task<ProfileSchemaCatalog> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ProfileSchemaCatalog(Array.Empty<ProfileSchemaDictionary>());
        }

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            ? await LoadFromCsvAsync(path, cancellationToken)
            : await LoadFromJsonAsync(path, cancellationToken);
    }

    public static async Task<ProfileSchemaCatalog> LoadFromJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("cabinets", out var cabinets))
        {
            var list = new List<ProfileSchemaDictionary>();
            foreach (var cabinet in cabinets.EnumerateArray())
            {
                var schema = ParseSchemaNode(cabinet);
                if (schema is not null)
                {
                    list.Add(schema);
                }
            }

            return new ProfileSchemaCatalog(list);
        }

        var single = ParseSchemaNode(root);
        return new ProfileSchemaCatalog(single is null ? Array.Empty<ProfileSchemaDictionary>() : new[] { single });
    }

    private static ProfileSchemaDictionary? ParseSchemaNode(JsonElement node)
    {
        var cabinet = node.TryGetProperty("cabinet", out var cabinetProp) ? cabinetProp.GetString() ?? string.Empty : string.Empty;
        var version = node.TryGetProperty("schemaVersion", out var versionProp) ? versionProp.GetString() ?? string.Empty : string.Empty;

        if (!node.TryGetProperty("fields", out var fieldsElement))
        {
            return null;
        }

        var fields = new List<ProfileSchemaField>();
        foreach (var fieldElement in fieldsElement.EnumerateArray())
        {
            var code = ReadString(fieldElement, "code");
            var name = ReadString(fieldElement, "name");
            var values = new List<ProfileSchemaValue>();
            if (fieldElement.TryGetProperty("values", out var valuesElement))
            {
                foreach (var valueElement in valuesElement.EnumerateArray())
                {
                    var valueCode = ReadString(valueElement, "code");
                    var label = ReadString(valueElement, "label");
                    if (!string.IsNullOrWhiteSpace(valueCode) || !string.IsNullOrWhiteSpace(label))
                    {
                        values.Add(new ProfileSchemaValue(valueCode, label));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(name))
            {
                fields.Add(new ProfileSchemaField(code, name, values));
            }
        }

        return new ProfileSchemaDictionary(cabinet, version, fields);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.String => property.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    public static async Task<ProfileSchemaCatalog> LoadFromCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var fields = new Dictionary<string, ProfileSchemaFieldBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = ParseCsvLine(line);
            if (parts.Count < 4)
            {
                continue;
            }

            var fieldCode = parts[0].Trim();
            var fieldName = parts[1].Trim();
            var valueCode = parts[2].Trim();
            var valueLabel = parts[3].Trim();

            var key = string.IsNullOrWhiteSpace(fieldCode) ? fieldName : fieldCode;
            if (!fields.TryGetValue(key, out var builder))
            {
                builder = new ProfileSchemaFieldBuilder(fieldCode, fieldName);
                fields[key] = builder;
            }

            if (!string.IsNullOrWhiteSpace(valueCode) || !string.IsNullOrWhiteSpace(valueLabel))
            {
                builder.Values.Add(new ProfileSchemaValue(valueCode, valueLabel));
            }
        }

        var schemaFields = fields.Values.Select(b => b.Build()).ToList();
        return new ProfileSchemaCatalog(new[] { new ProfileSchemaDictionary(string.Empty, string.Empty, schemaFields) });
    }

    private static List<string> ParseCsvLine(string line)
    {
        var results = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                results.Add(new string(current.ToArray()));
                current.Clear();
                continue;
            }

            current.Add(ch);
        }

        results.Add(new string(current.ToArray()));
        return results;
    }

    private sealed class ProfileSchemaFieldBuilder
    {
        public ProfileSchemaFieldBuilder(string code, string name)
        {
            Code = code;
            Name = name;
        }

        public string Code { get; }

        public string Name { get; }

        public List<ProfileSchemaValue> Values { get; } = new();

        public ProfileSchemaField Build()
        {
            return new ProfileSchemaField(Code, Name, Values);
        }
    }
}
