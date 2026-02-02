using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactsBearerTokens()
    {
        var input = "Auth failed for Bearer abc123.XYZ_TOKEN and other text.";
        var output = SensitiveDataRedactor.RedactBearerTokens(input);

        Assert.Equal("Auth failed for Bearer [REDACTED] and other text.", output);
    }
}
