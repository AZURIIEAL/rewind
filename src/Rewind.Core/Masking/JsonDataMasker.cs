using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rewind.Core.Masking;

public class JsonDataMasker : IMasker
{
    private static readonly Regex CreditCardPattern = new(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.Compiled);
    private static readonly Regex SsnPattern = new(@"\b\d{3}-?\d{2}-?\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"\b[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}\b", RegexOptions.Compiled);
    // Snapshots are files, never embedded in HTML, so keep non-ASCII text and <>&' readable instead of \uXXXX-escaping them.
    private static readonly JsonSerializerOptions OutputOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Regex DigitPattern = new(@"\d", RegexOptions.Compiled);

    // Values this masker already produced. Recognising them keeps masking idempotent, so replaying a
    // snapshot (and capturing the replay) doesn't turn "4111-XXXX-XXXX-1111" into a random-looking string.
    private static readonly Regex MaskedCardPattern = new(@"^\d{4}(-X{1,4})+-\d{4}$", RegexOptions.Compiled);
    private static readonly Regex MaskedSsnPattern = new(@"^XXX-XX-\d{4}$", RegexOptions.Compiled);

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

        if (root is JsonValue)
        {
            return MaskChild(key: null, root)?.ToJsonString(OutputOptions);
        }

        MaskNode(root, parentKey: null, sensitiveAncestorKey: null);
        return root.ToJsonString(OutputOptions);
    }

    public string MaskText(string text) =>
        _options.EnableMasking && !string.IsNullOrEmpty(text) ? MaskFreeText(text) : text;

    public IDictionary<string, string> MaskKeyValues(IDictionary<string, string> values)
    {
        var masked = new Dictionary<string, string>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            masked[key] = _options.EnableMasking
                ? MaskStringValue(key, value)
                : value;
        }

        return masked;
    }

    public string MaskQueryString(string queryString)
    {
        if (!_options.EnableMasking || string.IsNullOrEmpty(queryString))
        {
            return queryString;
        }

        var hasLeadingQuestionMark = queryString.StartsWith('?');
        var pairs = (hasLeadingQuestionMark ? queryString[1..] : queryString).Split('&');
        for (var i = 0; i < pairs.Length; i++)
        {
            var separatorIndex = pairs[i].IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var rawKey = pairs[i][..separatorIndex];
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            var value = Uri.UnescapeDataString(pairs[i][(separatorIndex + 1)..].Replace('+', ' '));
            var maskedValue = MaskStringValue(key, value);

            // Only re-encode pairs that changed, so untouched parameters keep their original encoding.
            if (maskedValue != value)
            {
                pairs[i] = $"{rawKey}={Uri.EscapeDataString(maskedValue)}";
            }
        }

        return (hasLeadingQuestionMark ? "?" : string.Empty) + string.Join('&', pairs);
    }

    // sensitiveAncestorKey is the nearest enclosing sensitive key (e.g. "credentials"), so every leaf beneath
    // it is masked even when its own name looks harmless. Children are only reassigned when masking produced
    // a new node: re-adding a node to the parent it already belongs to throws.
    private void MaskNode(JsonNode node, string? parentKey, string? sensitiveAncestorKey)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    var child = obj[key];
                    var effectiveKey = sensitiveAncestorKey is not null && !IsSensitiveKey(key) ? sensitiveAncestorKey : key;
                    var masked = MaskChild(effectiveKey, child);
                    if (!ReferenceEquals(masked, child))
                    {
                        obj[key] = masked;
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    var masked = MaskChild(parentKey, child);
                    if (!ReferenceEquals(masked, child))
                    {
                        array[i] = masked;
                    }
                }

                break;
        }
    }

    private JsonNode? MaskChild(string? key, JsonNode? child)
    {
        switch (child)
        {
            case JsonObject or JsonArray:
                MaskNode(child, key, key is not null && IsSensitiveKey(key) ? key : null);
                return child;

            case JsonValue value when value.TryGetValue<string>(out var stringValue):
                return MaskStringValue(key, stringValue);

            case JsonValue value when key is not null && IsSensitiveKey(key):
                return MaskNonStringValue(value);

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
        if (ContainsAny(key, "cvv", "cvc"))
        {
            return MaskDigits(value);
        }

        if (ContainsAny(key, "creditcard", "cardnumber"))
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

        if (ContainsAny(key, "cookie"))
        {
            return MaskCookie(value);
        }

        if (ContainsAny(key, "authorization"))
        {
            return MaskAuthorization(value);
        }

        return MaskGenericSecret(value);
    }

    // Keeps the JSON type so the payload still deserializes on replay: numbers keep their digit count, other values are left alone.
    private static JsonNode? MaskNonStringValue(JsonValue value)
    {
        if (value.GetValueKind() != JsonValueKind.Number)
        {
            return value;
        }

        return JsonNode.Parse(MaskDigits(value.ToJsonString()));
    }

    private string MaskFreeText(string value)
    {
        // Real card numbers always pass the Luhn checksum; requiring it keeps timestamps and order numbers intact.
        var masked = CreditCardPattern.Replace(value, match => IsLuhnValid(match.Value) ? MaskCreditCard(match.Value) : match.Value);
        masked = SsnPattern.Replace(masked, match => MaskSsn(match.Value));
        masked = EmailPattern.Replace(masked, match => MaskEmail(match.Value));
        return masked;
    }

    private bool IsSensitiveKey(string key) =>
        ContainsAny(key, _options.SensitiveKeyWords.ToArray());

    private static bool ContainsAny(string key, params string[] fragments)
    {
        var normalizedKey = NormalizeKey(key);
        return fragments.Any(fragment => normalizedKey.Contains(NormalizeKey(fragment), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string key) => key.Replace("-", string.Empty).Replace("_", string.Empty);

    private static bool IsLuhnValid(string value)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(value[i]))
            {
                continue;
            }

            var digit = value[i] - '0';
            if (doubleDigit && (digit *= 2) > 9)
            {
                digit -= 9;
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    private static string MaskDigits(string value) => DigitPattern.Replace(value, "1");

    // Masks each cookie value but keeps the names, so the header still parses into the same cookies on replay.
    private static string MaskCookie(string value)
    {
        var cookies = value.Split(';');
        for (var i = 0; i < cookies.Length; i++)
        {
            var separatorIndex = cookies[i].IndexOf('=');
            if (separatorIndex >= 0)
            {
                cookies[i] = cookies[i][..(separatorIndex + 1)] + MaskGenericSecret(cookies[i][(separatorIndex + 1)..]);
            }
        }

        return string.Join(';', cookies);
    }

    // Keeps the scheme ("Bearer", "Basic", ...) so a replayed request still reaches the same auth handler.
    private static string MaskAuthorization(string value)
    {
        var spaceIndex = value.IndexOf(' ');
        return spaceIndex > 0
            ? value[..(spaceIndex + 1)] + MaskGenericSecret(value[(spaceIndex + 1)..])
            : MaskGenericSecret(value);
    }

    private static string MaskCreditCard(string value)
    {
        if (MaskedCardPattern.IsMatch(value))
        {
            return value;
        }

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
        if (MaskedSsnPattern.IsMatch(value))
        {
            return value;
        }

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
