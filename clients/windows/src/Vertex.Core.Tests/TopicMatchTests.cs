using FluentAssertions;
using Vertex.Core.Protocol;
using Xunit;

namespace Vertex.Core.Tests;

public class TopicMatchTests
{
    [Theory]
    [InlineData("vpn/aws/iphone/in",        "vpn/aws/iphone/in",       true)]
    [InlineData("vpn/aws/iphone/in",        "vpn/+/iphone/in",         true)]
    [InlineData("vpn/aws/iphone/in",        "vpn/+/+/in",              true)]
    [InlineData("vpn/aws/iphone/in",        "vpn/aws/#",               true)]
    [InlineData("vpn/aws/iphone/in",        "#",                       true)]
    [InlineData("vpn/aws/iphone/in",        "vpn/aws/iphone",          false)]
    [InlineData("vpn/aws/iphone/in",        "vpn/aws/iphone/in/extra", false)]
    [InlineData("vpn/aws/iphone/in",        "vpn/sber/+/in",           false)]
    [InlineData("discovery/exits/aws",      "discovery/exits/+",       true)]
    [InlineData("discovery/exits/aws/sub",  "discovery/exits/+",       false)]
    [InlineData("discovery/exits/aws/sub",  "discovery/exits/#",       true)]
    [InlineData("a",                        "+",                       true)]
    [InlineData("",                         "",                        true)]
    [InlineData("a/b",                      "a/+",                     true)]
    public void Matches_TablesParityWithSwiftAndKotlin(string topic, string pattern, bool expected)
    {
        Topics.Matches(topic, pattern).Should().Be(expected);
    }
}
