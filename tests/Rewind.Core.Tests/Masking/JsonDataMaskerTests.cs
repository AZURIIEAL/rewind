using Rewind.Core.Masking;

namespace Rewind.Core.Tests.Masking;

public class JsonDataMaskerTests
{
    [Fact]
    public void MaskJson_MasksSensitiveKeysByName()
    {
        var masker = new JsonDataMasker();
        var json = """{"username":"jdoe","password":"hunter2"}""";

        var result = masker.MaskJson(json);

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("jdoe", result);
    }

    [Fact]
    public void MaskJson_MasksCreditCardPreservingFormat()
    {
        var masker = new JsonDataMasker();
        var json = """{"cardNumber":"4111111111111111"}""";

        var result = masker.MaskJson(json);

        Assert.Contains("4111-XXXX-XXXX-1111", result);
    }

    [Fact]
    public void MaskJson_MasksSsnByValuePatternEvenWithoutSensitiveKeyName()
    {
        var masker = new JsonDataMasker();
        var json = """{"taxId":"123-45-6789"}""";

        var result = masker.MaskJson(json);

        Assert.Contains("XXX-XX-6789", result);
        Assert.DoesNotContain("123-45-6789", result);
    }

    [Fact]
    public void MaskJson_MasksEmailKeepingDomain()
    {
        var masker = new JsonDataMasker();
        var json = """{"contactEmail":"john.doe@example.com"}""";

        var result = masker.MaskJson(json);

        Assert.Contains("@example.com", result);
        Assert.DoesNotContain("john.doe@example.com", result);
    }

    [Fact]
    public void MaskJson_RecursesIntoNestedObjectsAndArrays()
    {
        var masker = new JsonDataMasker();
        var json = """{"user":{"profile":{"password":"secret1"}},"tokens":["abc123token"]}""";

        var result = masker.MaskJson(json);

        Assert.DoesNotContain("secret1", result);
    }

    [Fact]
    public void MaskJson_ReturnsOriginal_WhenMaskingDisabled()
    {
        var options = new MaskingOptions { EnableMasking = false };
        var masker = new JsonDataMasker(options);
        var json = """{"password":"hunter2"}""";

        var result = masker.MaskJson(json);

        Assert.Equal(json, result);
    }

    [Fact]
    public void MaskJson_FallsBackToRegexMasking_ForNonJsonText()
    {
        var masker = new JsonDataMasker();
        var text = "Card 4111111111111111 on file";

        var result = masker.MaskJson(text);

        Assert.Contains("4111-XXXX-XXXX-1111", result);
    }

    [Fact]
    public void MaskHeaders_MasksAuthorizationHeader()
    {
        var masker = new JsonDataMasker();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abcdef123456",
            ["Content-Type"] = "application/json"
        };

        var result = masker.MaskHeaders(headers);

        Assert.NotEqual("Bearer abcdef123456", result["Authorization"]);
        Assert.Equal("application/json", result["Content-Type"]);
    }
}
