namespace Rewind.Core.Masking;

public interface IMasker
{
    string? MaskJson(string? json);
    IDictionary<string, string> MaskHeaders(IDictionary<string, string> headers);
}
