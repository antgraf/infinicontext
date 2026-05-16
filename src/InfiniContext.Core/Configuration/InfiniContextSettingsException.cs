namespace InfiniContext.Core.Configuration;

/// <summary>
/// Thrown when one or more settings values fail validation at load time. The host is expected to
/// print the messages and exit non-zero — bad config beats silent footgun.
/// </summary>
public sealed class InfiniContextSettingsException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public InfiniContextSettingsException(IReadOnlyList<string> errors)
        : base("Invalid InfiniContext settings: " + string.Join("; ", errors))
    {
        Errors = errors;
    }
}
