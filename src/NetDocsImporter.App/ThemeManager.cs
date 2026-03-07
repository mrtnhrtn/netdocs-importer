using System;
using System.Linq;
using System.Windows;

namespace NetDocsImporter.App;

public static class ThemeManager
{
    private const string LightThemePath = "Themes/LightTheme.xaml";
    private const string DarkThemePath = "Themes/DarkTheme.xaml";

    public static void ApplyTheme(bool isDarkMode)
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        var mergedDictionaries = application.Resources.MergedDictionaries;
        var existingTheme = mergedDictionaries.FirstOrDefault(dictionary =>
        {
            var source = dictionary.Source?.ToString() ?? string.Empty;
            return source.EndsWith(LightThemePath, StringComparison.OrdinalIgnoreCase) ||
                   source.EndsWith(DarkThemePath, StringComparison.OrdinalIgnoreCase);
        });

        if (existingTheme is not null)
        {
            mergedDictionaries.Remove(existingTheme);
        }

        var newThemeSource = isDarkMode ? DarkThemePath : LightThemePath;
        mergedDictionaries.Insert(
            0,
            new ResourceDictionary
            {
                Source = new Uri(newThemeSource, UriKind.Relative)
            });
    }
}
