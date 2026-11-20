using System.Net;
using UnderMineApi.Services.Registration;
using Xunit;

namespace UnderMineApi.Tests;

public class RegistrationIpTests
{
    private static string Key(string ip, int ipv4 = 32, int ipv6 = 64) =>
        RegistrationIp.ToPrefixKey(IPAddress.Parse(ip), ipv4, ipv6);

    [Fact]
    public void Ipv4Exact_IsUsedByDefault()
    {
        Assert.Equal("192.0.2.123/32", Key("192.0.2.123"));
    }

    [Fact]
    public void Ipv4Prefix_MasksTheAddress()
    {
        Assert.Equal("192.0.2.0/24", Key("192.0.2.123", 24));
        Assert.Equal("192.168.0.0/20", Key("192.168.15.7", 20));
    }

    [Fact]
    public void Ipv6_GroupsBy64()
    {
        Assert.Equal(Key("2001:db8:abcd:1234::1"), Key("2001:db8:abcd:1234::dead:beef"));
        Assert.Equal("2001:db8:abcd:1234::/64", Key("2001:db8:abcd:1234::1"));
    }

    [Fact]
    public void Ipv4MappedAddress_IsTreatedAsIpv4()
    {
        Assert.Equal("192.0.2.0/24", Key("::ffff:192.0.2.123", 24));
    }

    [Fact]
    public void MissingAddress_IsUnknown()
    {
        Assert.Equal("unknown", RegistrationIp.ToPrefixKey(null, 32, 64));
    }
}

public class RegistrationPolicyTests
{
    [Fact]
    public void AcceptsLauncherStyleDeviceId()
    {
        Assert.True(RegistrationPolicy.TryNormalizeDeviceId(
            new string('a', 64), 16, 128, out var normalized, out _));
        Assert.Equal(64, normalized.Length);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("contains space and is long enough")]
    [InlineData("contains\tcontrol")]
    public void RejectsInvalidDeviceIds(string value)
    {
        Assert.False(RegistrationPolicy.TryNormalizeDeviceId(value, 16, 128, out _, out _));
    }

    [Fact]
    public void TrimsAndEnforcesMaximum()
    {
        Assert.True(RegistrationPolicy.TryNormalizeDeviceId(
            "  abcdef0123456789  ", 16, 128, out var normalized, out _));
        Assert.Equal("abcdef0123456789", normalized);

        Assert.False(RegistrationPolicy.TryNormalizeDeviceId(
            new string('a', 129), 16, 128, out _, out _));
    }
}
