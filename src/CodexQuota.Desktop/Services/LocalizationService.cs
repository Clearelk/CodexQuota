using System.Windows;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;

namespace CodexQuota.Desktop.Services;

/// <summary>Projects the shared catalog into live WPF resources after a committed settings change.</summary>
public sealed class LocalizationService(Application application)
{
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.SimplifiedChinese;

    public void Apply(AppLanguage language)
    {
        CurrentLanguage = Enum.IsDefined(language) ? language : AppLanguage.SimplifiedChinese;
        foreach (string key in AppText.Keys)
        {
            application.Resources["Text." + key] = AppText.Template(CurrentLanguage, key);
        }
    }

    public static string Text(string key, params object?[] arguments) => AppText.Format(CurrentLanguage, key, arguments);
}
