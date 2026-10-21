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
    public void MaskKeyValues_MasksAuthorizationHeader()
    {
        var masker = new JsonDataMasker();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abcdef123456",
            ["Content-Type"] = "application/json"
        };

        var result = masker.MaskKeyValues(headers);

        Assert.NotEqual("Bearer abcdef123456", result["Authorization"]);
        Assert.StartsWith("Bearer ", result["Authorization"]);
        Assert.Equal("application/json", result["Content-Type"]);
    }

    [Fact]
    public void MaskKeyValues_MasksCookieValuesKeepingNames()
    {
        var masker = new JsonDataMasker();
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = ".AspNetCore.Session=abc123sessionvalue; theme=dark"
        };

        var result = masker.MaskKeyValues(headers);

        Assert.DoesNotContain("abc123sessionvalue", result["Cookie"]);
        Assert.DoesNotContain("dark", result["Cookie"]);
        Assert.Contains(".AspNetCore.Session=", result["Cookie"]);
        Assert.Contains(" theme=", result["Cookie"]);
    }

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("api_key")]
    [InlineData("X-Session-Id")]
    public void MaskKeyValues_MatchesSensitiveKeysIgnoringSeparators(string headerName)
    {
        var masker = new JsonDataMasker();
        var headers = new Dictionary<string, string> { [headerName] = "live-key-9f8e7d6c" };

        var result = masker.MaskKeyValues(headers);

        Assert.NotEqual("live-key-9f8e7d6c", result[headerName]);
    }

    [Fact]
    public void MaskKeyValues_MasksSensitiveValuesUnderNonSensitiveNames()
    {
        var masker = new JsonDataMasker();
        var headers = new Dictionary<string, string> { ["X-User-Email"] = "john.doe@example.com" };

        var result = masker.MaskKeyValues(headers);

        Assert.Equal("j***@example.com", result["X-User-Email"]);
    }

    [Fact]
    public void MaskQueryString_MasksSensitiveParametersOnly()
    {
        var masker = new JsonDataMasker();

        var result = masker.MaskQueryString("?page=2&access_token=abc123secret&q=hello%20world");

        Assert.DoesNotContain("abc123secret", result);
        Assert.StartsWith("?page=2&access_token=", result);
        Assert.EndsWith("&q=hello%20world", result);
    }

    [Fact]
    public void MaskQueryString_MasksValuePatternsInAnyParameter()
    {
        var masker = new JsonDataMasker();

        var result = masker.MaskQueryString("?contact=john.doe%40example.com");

        Assert.Equal("?contact=j%2A%2A%2A%40example.com", result);
    }

    [Fact]
    public void MaskQueryString_ReturnsOriginal_WhenMaskingDisabled()
    {
        var masker = new JsonDataMasker(new MaskingOptions { EnableMasking = false });

        var result = masker.MaskQueryString("?token=abc123secret");

        Assert.Equal("?token=abc123secret", result);
    }

    [Fact]
    public void MaskJson_KeepsNumberTypeForSensitiveNumericValues()
    {
        var masker = new JsonDataMasker();
        var json = """{"cvv":123,"sessionTimeout":3600}""";

        var result = masker.MaskJson(json);

        Assert.Equal("""{"cvv":111,"sessionTimeout":1111}""", result);
    }

    [Fact]
    public void MaskJson_MasksStringCvvKeepingLength()
    {
        var masker = new JsonDataMasker();
        var json = """{"card_cvc":"987"}""";

        var result = masker.MaskJson(json);

        Assert.Equal("""{"card_cvc":"111"}""", result);
    }
}
