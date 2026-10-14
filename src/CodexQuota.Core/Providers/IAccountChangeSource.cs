namespace CodexQuota.Core.Providers;

/// <summary>Invalidates cached account data as soon as the official account read or notification changes.</summary>
public interface IAccountChangeSource
{
    event Action? AccountChanged;
}
