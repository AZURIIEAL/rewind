using System.Text.Json.Nodes;
using Rewind.Core.Masking;

namespace Rewind.Core.Tests.Masking;

public class JsonDataMaskerEdgeCaseTests
{
    private readonly JsonDataMasker _masker = new();

    [Fact]
    public void MaskJson_MasksEveryLeafUnderASensitiveObjectKey()
    {
        var json = """{"credentials":{"user":"jdoe","pass":"hunter2","pin":1234}}""";

        var result = JsonNode.Parse(_masker.MaskJson(json)!)!;

        Assert.NotEqual("jdoe", (string?)result["credentials"]!["user"]);
        Assert.NotEqual("hunter2", (string?)result["credentials"]!["pass"]);
        Assert.Equal(1111, (int)result["credentials"]!["pin"]!);
    }

    [Fact]
    public void MaskJson_MasksSensitiveValuesInsideArraysOfObjects()
    {
        var json = """{"users":[{"name":"a","password":"p1"},{"name":"b","password":"p2"}]}""";

        var result = _masker.MaskJson(json)!;

        Assert.DoesNotContain("\"p1\"", result);
        Assert.DoesNotContain("\"p2\"", result);
        Assert.Contains("\"name\":\"a\"", result);
        Assert.Contains("\"name\":\"b\"", result);
    }

    [Fact]
    public void MaskJson_MasksEveryElementOfAnArrayUnderASensitiveKey()
    {
        var json = """{"apiKeys":["key-one-111","key-two-222"]}""";

        var result = _masker.MaskJson(json)!;

        Assert.DoesNotContain("key-one-111", result);
        Assert.DoesNotContain("key-two-222", result);
        Assert.Equal(2, JsonNode.Parse(result)!["apiKeys"]!.AsArray().Count);
    }

    [Fact]
    public void MaskJson_HandlesTopLevelArraysAndScalars()
    {
        Assert.DoesNotContain("hunter2", _masker.MaskJson("""[{"password":"hunter2"}]"""));
        Assert.Equal("42", _masker.MaskJson("42"));
        Assert.Equal("null", _masker.MaskJson("null"));
        Assert.Equal("\"XXX-XX-6789\"", _masker.MaskJson("\"123-45-6789\""));
    }

    [Fact]
    public void MaskJson_LeavesNonSensitiveTypesAndValuesUntouched()
    {
        var json = """{"id":7,"active":true,"price":19.99,"tags":["a","b"],"note":null,"nested":{"city":"Oslo"}}""";

        var result = _masker.MaskJson(json);

        Assert.Equal(json, result);
    }

    [Fact]
    public void MaskJson_KeepsBooleansAndNullsUnderSensitiveKeys()
    {
        var json = """{"hasPassword":true,"secretAnswer":null}""";

        var result = _masker.MaskJson(json);

        Assert.Equal(json, result);
    }

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("5500000000000004")]
    [InlineData("378282246310005")]
    public void MaskJson_MasksCardNumbersInAnyCommonFormat_EvenUnderInnocuousKeys(string card)
    {
        var json = $$"""{"note":"paid with {{card}} yesterday"}""";

        var result = _masker.MaskJson(json)!;

        Assert.DoesNotContain(card, result);
        Assert.Contains("XXXX", result);
        Assert.Contains("yesterday", result);
    }

    [Theory]
    [InlineData("1697040000000")]   // millisecond timestamp
    [InlineData("1234567890123")]   // order number
    public void MaskJson_DoesNotTreatNonLuhnDigitRunsAsCardNumbers(string value)
    {
        var json = $$"""{"reference":"{{value}}"}""";

        var result = _masker.MaskJson(json);

        Assert.Equal(json, result);
    }

    [Fact]
    public void MaskJson_StillMasksCardUnderCardKey_EvenWhenNotLuhnValid()
    {
        var json = """{"cardNumber":"1234567890123456"}""";

        var result = _masker.MaskJson(json)!;

        Assert.Contains("1234-XXXX-XXXX-3456", result);
    }

    [Fact]
    public void MaskJson_MasksMultipleEmailsAndSsnsInOneString()
    {
        var json = """{"message":"from a@x.com to bob@y.org re 123-45-6789"}""";

        var result = _masker.MaskJson(json)!;

        Assert.Contains("a***@x.com", result);
        Assert.Contains("b***@y.org", result);
        Assert.Contains("XXX-XX-6789", result);
        Assert.DoesNotContain("bob@", result);
    }

    [Fact]
    public void MaskJson_PreservesUnicodeContent()
    {
        var json = """{"name":"Zoë 日本","password":"pässwörd"}""";

        var result = JsonNode.Parse(_masker.MaskJson(json)!)!;

        Assert.Equal("Zoë 日本", (string?)result["name"]);
        Assert.NotEqual("pässwörd", (string?)result["password"]);
    }

    [Fact]
    public void MaskJson_DoesNotEscapeNonAsciiOrHtmlCharacters()
    {
        var json = """{"name":"Zoë 日本","query":"a<b && c>d","quote":"it's"}""";

        Assert.Equal(json, _masker.MaskJson(json));
    }

    [Fact]
    public void MaskJson_FallsBackToTextMasking_ForTruncatedJson()
    {
        var result = _masker.MaskJson("""{"email":"john.doe@example.com","password":"hun""");

        Assert.DoesNotContain("john.doe@example.com", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskJson_ReturnsBlankInputAsIs(string? input)
    {
        Assert.Equal(input, _masker.MaskJson(input));
    }

    [Fact]
    public void MaskJson_IsDeterministic()
    {
        var json = """{"token":"abc123","email":"john@example.com"}""";

        Assert.Equal(_masker.MaskJson(json), _masker.MaskJson(json));
    }

    [Fact]
    public void Masking_IsIdempotent_SoReplayedSnapshotsMatchTheOriginal()
    {
        var json = """
            {"cardNumber":"4111111111111111","ssn":"123-45-6789","email":"john@example.com","password":"hunter2",
             "token":"abc","cvv":"123","pin":4321,"note":"card 5500000000000004, ssn 987-65-4321, mail a@b.co"}
            """;

        var once = _masker.MaskJson(json);
        var twice = _masker.MaskJson(once);

        Assert.Equal(once, twice);

        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["Cookie"] = "a=1; b=2" };
        var maskedHeaders = _masker.MaskKeyValues(headers);
        Assert.Equal(maskedHeaders, _masker.MaskKeyValues(maskedHeaders));

        var query = _masker.MaskQueryString("?token=abc&mail=a%40b.com");
        Assert.Equal(query, _masker.MaskQueryString(query));
    }

    [Fact]
    public void MaskJson_UsesCustomKeywords()
    {
        var masker = new JsonDataMasker(new MaskingOptions { SensitiveKeyWords = { "iban" } });

        var result = masker.MaskJson("""{"payoutIban":"NO9386011117947"}""")!;

        Assert.DoesNotContain("NO9386011117947", result);
    }

    [Fact]
    public void MaskQueryString_HandlesEdgeCases()
    {
        Assert.Equal("", _masker.MaskQueryString(""));
        Assert.Equal("?flag", _masker.MaskQueryString("?flag"));
        Assert.Equal("?a=&b=1", _masker.MaskQueryString("?a=&b=1"));
        Assert.Equal("page=2", _masker.MaskQueryString("page=2"));
        Assert.DoesNotContain("hunter2", _masker.MaskQueryString("?password=hunter2&password=hunter2"));
        Assert.Equal("?q=bad%ZZescape", _masker.MaskQueryString("?q=bad%ZZescape"));
    }

    [Fact]
    public void MaskQueryString_DecodesPlusAndPercentBeforeMatching()
    {
        var result = _masker.MaskQueryString("?note=ssn+123-45-6789&mail=a%40b.com");

        Assert.DoesNotContain("123-45-6789", result);
        Assert.DoesNotContain("a%40b.com", result);
    }

    [Fact]
    public void MaskKeyValues_IsCaseInsensitiveAndReturnsCaseInsensitiveDictionary()
    {
        var result = _masker.MaskKeyValues(new Dictionary<string, string> { ["AUTHORIZATION"] = "Basic dXNlcjpwYXNz" });

        Assert.StartsWith("Basic ", result["authorization"]);
        Assert.DoesNotContain("dXNlcjpwYXNz", result["Authorization"]);
    }

    [Fact]
    public void MaskKeyValues_LeavesOrdinaryHeadersAlone()
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["User-Agent"] = "Mozilla/5.0 (X11; Linux x86_64)",
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        var result = _masker.MaskKeyValues(headers);

        Assert.Equal(headers, result);
    }

    [Theory]
    [InlineData("cardNumber", "1234")]          // too short to be a card
    [InlineData("ssn", "12-34")]                 // wrong digit count for an SSN
    [InlineData("creditCard", "on file")]
    public void MaskJson_FallsBackToAGenericSecret_WhenAKeyedValueDoesNotLookLikeItsType(string key, string value)
    {
        var json = $$"""{"{{key}}":"{{value}}"}""";

        var masked = (string?)JsonNode.Parse(_masker.MaskJson(json)!)![key];

        Assert.NotEqual(value, masked);
        Assert.True(masked!.Length >= 12, "generic secrets have a minimum length so short values aren't guessable");
        Assert.Equal(masked, (string?)JsonNode.Parse(_masker.MaskJson(json)!)![key]);
    }

    [Fact]
    public void MaskJson_MasksEmailsKeepingTheDomain_WhenEmailIsAConfiguredKeyword()
    {
        var masker = new JsonDataMasker(new MaskingOptions { SensitiveKeyWords = { "email" } });

        var result = JsonNode.Parse(masker.MaskJson("""{"email":"john@example.com","backupEmail":"not-an-address"}""")!)!;

        Assert.Equal("j***@example.com", (string?)result["email"]);
        Assert.NotEqual("not-an-address", (string?)result["backupEmail"]);
        Assert.DoesNotContain("@", (string?)result["backupEmail"]);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("x-api-key")]
    [InlineData("Credit_Card")]
    [InlineData("SESSION")]
    public void MaskJson_MatchesSensitiveKeysIgnoringCaseAndSeparators(string key)
    {
        var json = $$"""{"{{key}}":"value-that-must-not-leak"}""";

        Assert.DoesNotContain("value-that-must-not-leak", _masker.MaskJson(json));
    }

    [Fact]
    public void MaskJson_PreservesPropertyOrder_SoDiffsAgainstTheOriginalStayReadable()
    {
        var json = """{"z":1,"password":"x","a":2,"token":"y","m":3}""";

        var keys = JsonNode.Parse(_masker.MaskJson(json)!)!.AsObject().Select(property => property.Key);

        Assert.Equal(new[] { "z", "password", "a", "token", "m" }, keys);
    }

    [Fact]
    public void Masking_CanBeDisabledForEveryEntryPoint()
    {
        var masker = new JsonDataMasker(new MaskingOptions { EnableMasking = false });

        Assert.Equal("a@b.com", masker.MaskText("a@b.com"));
        Assert.Equal("Bearer x", masker.MaskKeyValues(new Dictionary<string, string> { ["Authorization"] = "Bearer x" })["Authorization"]);
        Assert.Equal("?token=x", masker.MaskQueryString("?token=x"));
    }
}
