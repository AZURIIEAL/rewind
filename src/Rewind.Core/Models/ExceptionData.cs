namespace Rewind.Core.Models;

public class ExceptionData
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public ExceptionData? InnerException { get; set; }

    public static ExceptionData FromException(Exception ex)
    {
        return new ExceptionData
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace,
            InnerException = ex.InnerException != null ? FromException(ex.InnerException) : null
        };
    }
}