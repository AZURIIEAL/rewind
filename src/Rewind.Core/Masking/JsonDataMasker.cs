using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rewind.Core.Masking;

public class JsonDataMasker : IMasker
{
    private static readonly Regex CreditCardPattern = new(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.Compiled);
    private static readonly Regex SsnPattern = new(@"\b\d{3}-?\d{2}-?\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"\b[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}\b", RegexOptions.Compiled);

    private readonly MaskingOptions _options;

    public JsonDataMasker(MaskingOptions? options = null)
    {
        _options = options ?? new MaskingOptions();
    }

    public string? MaskJson(string? json)
    {
        if (!_options.EnableMasking || string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return MaskFreeText(json);
        }

        if (root is null)
        {
            return json;
        }

        MaskNode(root, parentKey: null);
        return root.ToJsonString();
    }

    public IDictionary<string, string> MaskHeaders(IDictionary<string, string> headers)
    {
        var masked = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            masked[key] = _options.EnableMasking && IsSensitiveKey(key)
                ? MaskValueForKey(key, value)
                : value;
        }

        return masked;
    }

    private void MaskNode(JsonNode node, string? parentKey)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    obj[key] = MaskChild(key, obj[key]);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = MaskChild(parentKey, array[i]);
                }

                break;
        }
    }

    private JsonNode? MaskChild(string? key, JsonNode? child)
    {
        switch (child)
        {
            case JsonObject or JsonArray:
                MaskNode(child, key);
                return child;

            case JsonValue value when value.TryGetValue<string>(out var stringValue):
                return MaskStringValue(key, stringValue);

            case JsonValue when key is not null && IsSensitiveKey(key):
                return "***MASKED***";

            default:
                return child;
        }
    }

    private string MaskStringValue(string? key, string value)
    {
        if (key is not null && IsSensitiveKey(key))
        {
            return MaskValueForKey(key, value);
        }

        return MaskFreeText(value);
    }

    private string MaskValueForKey(string key, string value)
    {
        if (ContainsAny(key, "creditcard", "cardnumber", "cvv"))
        {
            return MaskCreditCard(value);
        }

        if (ContainsAny(key, "ssn"))
        {
            return MaskSsn(value);
        }

        if (ContainsAny(key, "email"))
        {
            return MaskEmail(value);
        }

        if (ContainsAny(key, "password"))
        {
            return "P@ssw0rd-Replaced1!";
        }

        return MaskGenericSecret(value);
    }

    private string MaskFreeText(string value)
    {
        var masked = CreditCardPattern.Replace(value, match => MaskCreditCard(match.Value));
        masked = SsnPattern.Replace(masked, match => MaskSsn(match.Value));
        masked = EmailPattern.Replace(masked, match => MaskEmail(match.Value));
        return masked;
    }

    private bool IsSensitiveKey(string key) =>
        _options.SensitiveKeyWords.Any(keyword => key.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string key, params string[] fragments) =>
        fragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string MaskCreditCard(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 12)
        {
            return MaskGenericSecret(value);
        }

        var first = digits[..4];
        var last = digits[^4..];
        var groups = new List<string> { first };

        var remaining = digits.Length - 8;
        while (remaining > 0)
        {
            var groupSize = Math.Min(4, remaining);
            groups.Add(new string('X', groupSize));
            remaining -= groupSize;
        }

        groups.Add(last);
        return string.Join('-', groups);
    }

    private static string MaskSsn(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length != 9)
        {
            return MaskGenericSecret(value);
        }

        return $"XXX-XX-{digits[^4..]}";
    }

    private static string MaskEmail(string value)
    {
        var atIndex = value.IndexOf('@');
        if (atIndex <= 0)
        {
            return MaskGenericSecret(value);
        }

        var domain = value[(atIndex + 1)..];
        return $"{value[0]}***@{domain}";
    }

    private static string MaskGenericSecret(string value)
    {
        const string chars = "abcdef0123456789";
        var length = Math.Max(value.Length, 12);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            builder.Append(chars[(i * 7 + 3) % chars.Length]);
        }

        return builder.ToString();
    }
}
