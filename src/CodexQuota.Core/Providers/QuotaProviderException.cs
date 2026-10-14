using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Core.Providers;

/// <summary>
/// Error raised by a provider or its transport. Message is always a safe, sanitized
/// explanation suitable for display; raw helper output is never attached.
/// </summary>
public sealed class QuotaProviderException : Exception
{
    public QuotaProviderException(ProviderFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ProviderFailureKind Kind { get; }
    public AppMessage? PresentationMessage { get; }

    public QuotaProviderException(ProviderFailureKind kind, AppMessage message, Exception? innerException = null)
        : this(kind, message.Format(), innerException) => PresentationMessage = message;

    public static QuotaProviderException CliNotFound(AppMessage message) => new(ProviderFailureKind.CliNotFound, message);
    public static QuotaProviderException NodeNotFound(AppMessage message) => new(ProviderFailureKind.NodeNotFound, message);
    public static QuotaProviderException NotSignedIn(AppMessage message) => new(ProviderFailureKind.NotSignedIn, message);
    public static QuotaProviderException UnsupportedAccount(AppMessage message) => new(ProviderFailureKind.UnsupportedAccount, message);
    public static QuotaProviderException UnsupportedMethod(AppMessage message) => new(ProviderFailureKind.UnsupportedMethod, message);
    public static QuotaProviderException InvalidData(AppMessage message) => new(ProviderFailureKind.InvalidData, message);
    public static QuotaProviderException Transport(AppMessage message, Exception? innerException = null) => new(ProviderFailureKind.Transport, message, innerException);
    public static QuotaProviderException Timeout(AppMessage message) => new(ProviderFailureKind.Timeout, message);
    public static QuotaProviderException ProcessExited(AppMessage message) => new(ProviderFailureKind.ProcessExited, message);

    public static QuotaProviderException CliNotFound(string message) =>
        new(ProviderFailureKind.CliNotFound, message);

    public static QuotaProviderException NodeNotFound(string message) =>
        new(ProviderFailureKind.NodeNotFound, message);

    public static QuotaProviderException NotSignedIn(string message) =>
        new(ProviderFailureKind.NotSignedIn, message);

    public static QuotaProviderException UnsupportedAccount(string message) =>
        new(ProviderFailureKind.UnsupportedAccount, message);

    public static QuotaProviderException UnsupportedMethod(string message) =>
        new(ProviderFailureKind.UnsupportedMethod, message);

    public static QuotaProviderException InvalidData(string message) =>
        new(ProviderFailureKind.InvalidData, message);

    public static QuotaProviderException Transport(string message, Exception? innerException = null) =>
        new(ProviderFailureKind.Transport, message, innerException);

    public static QuotaProviderException Timeout(string message) =>
        new(ProviderFailureKind.Timeout, message);

    public static QuotaProviderException ProcessExited(string message) =>
        new(ProviderFailureKind.ProcessExited, message);
}
