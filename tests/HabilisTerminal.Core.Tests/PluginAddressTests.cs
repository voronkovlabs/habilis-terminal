using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #381: "cd name" in a plugin panel is relative to the panel's location, as in Far.</summary>
public class PluginAddressTests
{
    [Theory]
    [InlineData("s3://bucket/", "data/run-1/model.ipynb", "s3://bucket/data/run-1/model.ipynb")]
    [InlineData("s3://bucket/", "data/run-1/", "s3://bucket/data/run-1/")]
    [InlineData("s3://bucket/data/", "..", "s3://bucket/")]
    [InlineData("s3://bucket/", "..", "s3://")]
    [InlineData("s3://bucket/a/b/", "../c", "s3://bucket/a/c")]
    [InlineData("s3://bucket/a/", "/other/x/", "s3://other/x/")]
    [InlineData("s3://bucket/a/", @"b\c\", "s3://bucket/a/b/c/")]
    [InlineData("s3://", "bucket", "s3://bucket")]
    [InlineData("s3://bucket/a/", "./b", "s3://bucket/a/b")]
    public void A_relative_name_resolves_against_the_location(string location, string relative, string expected) =>
        Assert.Equal(expected, PluginAddress.Resolve(location, relative));

    [Fact]
    public void A_location_without_a_scheme_has_nothing_to_resolve_against() =>
        Assert.Null(PluginAddress.Resolve("sqs:default/us-east-1", "queue"));
}
