namespace Rewind.Core.Masking;

public interface IMasker
{
    // Masks a JSON document; falls back to MaskText if the input isn't valid JSON.
    string? MaskJson(string? json);

    // Masks sensitive value patterns (cards, SSNs, emails) anywhere in free text.
    string MaskText(string text);

    // Masks "a=1&b=2"-style data (query strings and form-encoded bodies), by key name and by value.
    string MaskQueryString(string queryString);

    // Masks name/value pairs such as headers or route values, by key name and by value.
    IDictionary<string, string> MaskKeyValues(IDictionary<string, string> values);
}
