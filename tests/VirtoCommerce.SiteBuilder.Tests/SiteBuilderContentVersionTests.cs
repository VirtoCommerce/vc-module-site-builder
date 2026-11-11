using System;
using System.Text;
using VirtoCommerce.SiteBuilder.Core.Models;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderContentVersionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    [InlineData(8191)]
    [InlineData(100000)]
    public void StreamedGetAndStringSaveUseTheSameVersionForUnicodeContent(int prefixLength)
    {
        var group = new GroupedSiteBuilderPage { Id = "group", StoreId = "store", CreatedDate = new DateTime(2026, 1, 1) };
        var content = new string('a', prefixLength) + "😀 Кириллица 日本語 \"quoted\"\r\n\\";

        Assert.Equal(SiteBuilderContentVersion.Create(group, content),
            SiteBuilderContentVersion.CreateFromUtf8(group, Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void EmptyDocumentHasTheSameVersionInBothPathsAndDiffersFromAnUnseededGroup()
    {
        var group = new GroupedSiteBuilderPage { Id = "group", StoreId = "store" };

        Assert.Equal(SiteBuilderContentVersion.Create(group, ""), SiteBuilderContentVersion.CreateFromUtf8(group, []));
        Assert.NotEqual(SiteBuilderContentVersion.Create(group, null), SiteBuilderContentVersion.CreateFromUtf8(group, []));
    }
}
