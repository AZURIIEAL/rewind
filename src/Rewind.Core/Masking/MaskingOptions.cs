namespace Rewind.Core.Masking;

public class MaskingOptions
{
    public bool EnableMasking { get; set; } = true;
    public List<string> SensitiveKeyWords { get; set; } = new()
    {
        "password", "secret", "token", "ssn", "cvv", "creditcard", "authorization", "apikey"
    };
}
