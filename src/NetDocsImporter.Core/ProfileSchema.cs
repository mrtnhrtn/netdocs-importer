namespace NetDocsImporter.Core;

public sealed class ProfileSchemaCatalog
{
    public ProfileSchemaCatalog(IReadOnlyList<ProfileSchemaDictionary> schemas)
    {
        Schemas = schemas ?? Array.Empty<ProfileSchemaDictionary>();
    }

    public IReadOnlyList<ProfileSchemaDictionary> Schemas { get; }

    public ProfileSchemaDictionary? GetForCabinet(string? cabinetName)
    {
        if (Schemas.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cabinetName))
        {
            var match = Schemas.FirstOrDefault(s => s.CabinetName.Equals(cabinetName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Schemas[0];
    }
}

public sealed class ProfileSchemaDictionary
{
    public ProfileSchemaDictionary(string cabinetName, string schemaVersion, IReadOnlyList<ProfileSchemaField> fields)
    {
        CabinetName = cabinetName;
        SchemaVersion = schemaVersion;
        Fields = fields ?? Array.Empty<ProfileSchemaField>();

        _fieldsByCode = Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Code))
            .ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);

        _fieldsByName = Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        _fieldsByExactName = Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    private readonly IReadOnlyDictionary<string, ProfileSchemaField> _fieldsByCode;
    private readonly IReadOnlyDictionary<string, ProfileSchemaField> _fieldsByName;
    private readonly IReadOnlyDictionary<string, ProfileSchemaField> _fieldsByExactName;

    public string CabinetName { get; }

    public string SchemaVersion { get; }

    public IReadOnlyList<ProfileSchemaField> Fields { get; }

    public bool TryResolveFieldName(string fieldCode, out string fieldName)
    {
        fieldName = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldCode))
        {
            return false;
        }

        if (_fieldsByCode.TryGetValue(fieldCode.Trim(), out var field))
        {
            fieldName = field.Name;
            return true;
        }

        return false;
    }

    public bool TryResolveFieldCode(string fieldName, out string fieldCode)
    {
        fieldCode = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (_fieldsByName.TryGetValue(fieldName.Trim(), out var field))
        {
            fieldCode = field.Code;
            return true;
        }

        return false;
    }

    public bool TryResolveCanonicalFieldName(string fieldKey, out string canonicalName)
    {
        canonicalName = string.Empty;
        if (TryGetField(fieldKey, out var field))
        {
            canonicalName = field.Name;
            return true;
        }

        return false;
    }

    public bool IsExactFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return _fieldsByExactName.ContainsKey(fieldName.Trim());
    }

    public bool TryGetField(string fieldKey, out ProfileSchemaField field)
    {
        field = null!;
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            return false;
        }

        if (_fieldsByCode.TryGetValue(fieldKey.Trim(), out var byCode))
        {
            field = byCode;
            return true;
        }

        if (_fieldsByName.TryGetValue(fieldKey.Trim(), out var byName))
        {
            field = byName;
            return true;
        }

        return false;
    }

    public bool TryResolveValueLabel(string fieldKey, string valueCode, out string valueLabel)
    {
        valueLabel = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(valueCode))
        {
            return false;
        }

        if (!TryGetField(fieldKey, out var field))
        {
            return false;
        }

        if (field.ValuesByCode.TryGetValue(valueCode.Trim(), out var label))
        {
            valueLabel = label;
            return true;
        }

        return false;
    }

    public bool TryResolveValueCode(string fieldKey, string valueLabel, out string valueCode)
    {
        valueCode = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(valueLabel))
        {
            return false;
        }

        if (!TryGetField(fieldKey, out var field))
        {
            return false;
        }

        if (field.ValuesByLabel.TryGetValue(valueLabel.Trim(), out var code))
        {
            valueCode = code;
            return true;
        }

        return false;
    }
}

public sealed class ProfileSchemaField
{
    public ProfileSchemaField(
        string code,
        string name,
        IReadOnlyList<ProfileSchemaValue> values,
        bool isLookup = false,
        bool isMultiValue = false)
    {
        Code = code;
        Name = name;
        Values = values ?? Array.Empty<ProfileSchemaValue>();
        IsLookup = isLookup || Values.Count > 0;
        IsMultiValue = isMultiValue;

        ValuesByCode = Values
            .Where(v => !string.IsNullOrWhiteSpace(v.Code))
            .ToDictionary(v => v.Code, v => v.Label, StringComparer.OrdinalIgnoreCase);

        ValuesByLabel = Values
            .Where(v => !string.IsNullOrWhiteSpace(v.Label))
            .ToDictionary(v => v.Label, v => v.Code, StringComparer.OrdinalIgnoreCase);
    }

    public string Code { get; }

    public string Name { get; }

    public IReadOnlyList<ProfileSchemaValue> Values { get; }

    public bool IsLookup { get; }

    public bool IsMultiValue { get; }

    public IReadOnlyDictionary<string, string> ValuesByCode { get; }

    public IReadOnlyDictionary<string, string> ValuesByLabel { get; }

    public bool ContainsValueCode(string valueCode)
    {
        return !string.IsNullOrWhiteSpace(valueCode) && ValuesByCode.ContainsKey(valueCode.Trim());
    }
}

public sealed class ProfileSchemaValue
{
    public ProfileSchemaValue(string code, string label, string? parentKey = null)
    {
        Code = code;
        Label = label;
        ParentKey = parentKey;
    }

    public string Code { get; }

    public string Label { get; }

    public string? ParentKey { get; }
}
