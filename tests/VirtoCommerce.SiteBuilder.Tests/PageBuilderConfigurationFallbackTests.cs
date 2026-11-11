using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Web;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class PageBuilderConfigurationFallbackTests
{
    [Fact]
    public void AddSiteBuilderOptions_BothSections_SiteBuilderWins()
    {
        var (services, keys) = AddOptions(new()
        {
            ["SiteBuilder:ThemeSnapshot:SizeWarningThreshold"] = "100",
            ["PageBuilder:ThemeSnapshot:SizeWarningThreshold"] = "200",
            ["SiteBuilder:GitContent:BaseBranch"] = "new",
            ["PageBuilder:GitContent:BaseBranch"] = "old",
        });

        Assert.Equal(100, services.GetRequiredService<IOptions<ThemeSnapshotOptions>>().Value.SizeWarningThreshold);
        Assert.Equal("new", services.GetRequiredService<IOptions<GitContentOptions>>().Value.BaseBranch);
        Assert.Empty(keys);
    }

    [Fact]
    public void AddSiteBuilderOptions_OnlyPageBuilderSections_BindsThem()
    {
        var (services, keys) = AddOptions(new()
        {
            ["PageBuilder:ThemeSnapshot:SizeWarningThreshold"] = "200",
            ["PageBuilder:GitContent:BaseBranch"] = "old",
            // an empty new section counts as absent
            ["SiteBuilder:GitContent"] = "",
        });

        Assert.Equal(200, services.GetRequiredService<IOptions<ThemeSnapshotOptions>>().Value.SizeWarningThreshold);
        Assert.Equal("old", services.GetRequiredService<IOptions<GitContentOptions>>().Value.BaseBranch);
        Assert.Equal(
            [new("PageBuilder:GitContent", GitContentOptions.SectionName), new("PageBuilder:ThemeSnapshot", ThemeSnapshotOptions.SectionName)],
            keys);
    }

    [Fact]
    public void AddSiteBuilderOptions_InvalidPageBuilderGitContent_FailsFast()
    {
        Assert.Throws<InvalidOperationException>(() => AddOptions(new()
        {
            ["PageBuilder:GitContent:Enabled"] = "true",
        }));
    }

    [Fact]
    public void WarnAboutPageBuilderConfiguration_PageBuilderKeysUsed_LogsOneWarning()
    {
        var logger = new RecordingLogger();

        Module.WarnAboutPageBuilderConfiguration(logger, [
            new("PageBuilder:GitContent", "SiteBuilder:GitContent"),
            new("PageBuilder:ThemeSnapshot", "SiteBuilder:ThemeSnapshot"),
        ]);
        Module.WarnAboutPageBuilderConfiguration(logger, []);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("PageBuilder:GitContent -> SiteBuilder:GitContent", warning.Message);
        Assert.Contains("PageBuilder:ThemeSnapshot -> SiteBuilder:ThemeSnapshot", warning.Message);
    }

    private static (ServiceProvider Services, List<Module.RenamedConfigurationKey> Keys) AddOptions(Dictionary<string, string> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        var keys = Module.AddSiteBuilderOptions(services, configuration);
        return (services.BuildServiceProvider(), keys);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
