using RtspRecServer.Server.Services;

namespace RtspRecServer.Tests.Services;

public class ListenAddressResolverTests
{
    [Fact]
    public void Resolve_DefaultHosts_Unchanged()
    {
        var plan = ListenAddressResolver.Resolve(8080, ["0.0.0.0"]);

        Assert.Equal(["http://0.0.0.0:8080"], plan.Urls);
        Assert.Equal(["0.0.0.0"], plan.Hosts);
        Assert.Empty(plan.Hints);
    }

    [Fact]
    public void Resolve_Ipv6Any_BuildsBracketedUrl()
    {
        var plan = ListenAddressResolver.Resolve(8080, ["::"]);

        Assert.Equal(["http://[::]:8080"], plan.Urls);
        Assert.Equal(["::"], plan.Hosts);
        Assert.Empty(plan.Hints);
    }

    [Fact]
    public void Resolve_Ipv6Any_CoversIpv4Addresses()
    {
        // :: 双栈监听已覆盖所有 IPv4 目标，IPv4 条目应合并掉，避免同端口重复绑定
        var plan = ListenAddressResolver.Resolve(8080, ["0.0.0.0", "::", "192.168.1.5"]);

        Assert.Equal(["http://[::]:8080"], plan.Urls);
        Assert.Equal(["::"], plan.Hosts);
        Assert.Contains(plan.Hints, hint => hint.Contains("0.0.0.0"));
        Assert.Contains(plan.Hints, hint => hint.Contains("192.168.1.5"));
    }

    [Fact]
    public void Resolve_Ipv6Loopback_CoexistsWithIpv4()
    {
        // 具体 IPv6 地址与 IPv4 地址分属不同协议栈，可以共存
        var plan = ListenAddressResolver.Resolve(8080, ["127.0.0.1", "::1"]);

        Assert.Equal(["http://127.0.0.1:8080", "http://[::1]:8080"], plan.Urls);
        Assert.Empty(plan.Hints);
    }

    [Fact]
    public void Resolve_Ipv4Any_CollapsesSpecificIpv4()
    {
        var plan = ListenAddressResolver.Resolve(8080, ["0.0.0.0", "192.168.1.5"]);

        Assert.Equal(["http://0.0.0.0:8080"], plan.Urls);
        Assert.Contains(plan.Hints, hint => hint.Contains("192.168.1.5"));
    }

    [Fact]
    public void NormalizeHosts_StripsBracketsAndNormalizes()
    {
        var hosts = ListenAddressResolver.NormalizeHosts(["[::1]", " 0.0.0.0 ", "", "[2001:DB8::1]"]);

        Assert.Equal(["::1", "0.0.0.0", "2001:db8::1"], hosts);
    }

    [Fact]
    public void NormalizeHosts_RejectsInvalidIpv6()
    {
        foreach (var bad in new[] { ":::", "zz::12", "[::1", "1:2:3" })
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ListenAddressResolver.NormalizeHosts([bad]));
            Assert.Contains("IPv6", ex.Message);
        }
    }

    [Fact]
    public void NormalizeHosts_DeduplicatesBracketForms()
    {
        var hosts = ListenAddressResolver.NormalizeHosts(["::1", "[::1]", "[::1]"]);

        Assert.Equal(["::1"], hosts);
    }

    [Fact]
    public void NormalizeHosts_KeepsHostnames()
    {
        // 非 IP 字面量（主机名）保持透传，交由 Kestrel 解析
        var hosts = ListenAddressResolver.NormalizeHosts(["myhost.local"]);

        Assert.Equal(["myhost.local"], hosts);
    }
}
