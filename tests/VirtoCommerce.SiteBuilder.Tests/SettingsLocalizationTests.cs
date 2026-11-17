using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SiteBuilder.Core;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SettingsLocalizationTests
{
    /// <summary>Page Builder's migration flags keep its names, so a migration it already ran is not run again.</summary>
    private const string PageBuilderMigrationPrefix = "VirtoCommerce.PageBuilderModule.Migration.";

    public static TheoryData<string> LocaleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(LocalizationsPath, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Fact]
    public void SettingNames_StartWithTheModuleName()
    {
        var names = ModuleConstants.Settings.AllSettings
            .Select(x => x.Name)
            .Where(x => !x.StartsWith(PageBuilderMigrationPrefix, StringComparison.Ordinal));

        Assert.All(names, name => Assert.StartsWith("SiteBuilder.", name, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void VisibleSettings_HaveATitleAndDescription(string localeFile)
    {
        var settings = JObject.Parse(File.ReadAllText(Path.Combine(LocalizationsPath, localeFile)))["settings"];

        var missing = ModuleConstants.Settings.AllSettings
            .Where(x => !x.IsHidden)
            .SelectMany(x => MissingTexts(settings, x))
            .ToList();

        Assert.Empty(missing);
    }

    private static string LocalizationsPath => Path.Combine(AppContext.BaseDirectory, "Localizations");

    private static IEnumerable<string> MissingTexts(JToken settings, SettingDescriptor setting)
    {
        var node = setting.Name.Split('.').Aggregate(settings, (current, part) => current?[part]);

        foreach (var text in new[] { "title", "description" })
        {
            if (string.IsNullOrWhiteSpace(node?[text]?.ToString()))
            {
                yield return $"{setting.Name}.{text}";
            }
        }
    }
}
