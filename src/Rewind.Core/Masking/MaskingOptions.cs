namespace Rewind.Core.Masking;

public class MaskingOptions
{
    public bool EnableMasking { get; set; } = true;

    // Matched as substrings of key names, ignoring case, '-' and '_' (so "apikey" also catches "X-Api-Key" and "api_key").
    public List<string> SensitiveKeyWords { get; set; } = new()
    {
        "password", "secret", "token", "ssn", "cvv", "cvc", "creditcard", "cardnumber",
        "authorization", "apikey", "cookie", "session", "credential"
    };
}
