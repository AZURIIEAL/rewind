using Rewind.Core.Masking;

namespace Rewind.AspNetCore.Options;

public class RewindOptions
{
    public string OutputDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "rewind");
    public MaskingOptions Masking { get; set; } = new();
}