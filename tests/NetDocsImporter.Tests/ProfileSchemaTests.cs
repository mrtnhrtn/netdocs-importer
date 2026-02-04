using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class ProfileSchemaTests
{
    [Fact]
    public async Task LoadsSchemaFromJsonAndResolvesValues()
    {
        var tempRoot = CreateTempRoot();
        var schemaPath = Path.Combine(tempRoot, "schema.json");
        var json = """
            {
              "version": 1,
              "cabinet": "CaseCabinet",
              "schemaVersion": "2024.1",
              "fields": [
                {
                  "code": "1001",
                  "name": "Document Type",
                  "values": [
                    { "code": "2001", "label": "Correspondence" }
                  ]
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(schemaPath, json);

        var catalog = await ProfileSchemaLoader.LoadFromJsonAsync(schemaPath);
        var schema = catalog.GetForCabinet("CaseCabinet");

        Assert.NotNull(schema);
        Assert.True(schema!.TryResolveFieldName("1001", out var fieldName));
        Assert.Equal("Document Type", fieldName);
        Assert.True(schema.TryResolveValueLabel("1001", "2001", out var valueLabel));
        Assert.Equal("Correspondence", valueLabel);
        Assert.True(schema.TryResolveFieldCode("Document Type", out var fieldCode));
        Assert.Equal("1001", fieldCode);
        Assert.True(schema.TryResolveValueCode("Document Type", "Correspondence", out var valueCode));
        Assert.Equal("2001", valueCode);

        CleanupTempRoot(tempRoot);
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
