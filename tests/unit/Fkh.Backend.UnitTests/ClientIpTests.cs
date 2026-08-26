using Xunit;

namespace Fkh.Backend.UnitTests;

public class ClientIpTests
{
    [Fact]
    public void Uses_last_value_and_ignores_spoofed_leftmost()
    {
        // Caller injected "1.2.3.4"; Azure appended the real socket IP last.
        Assert.Equal("203.0.113.9", FunctionBase.ExtractClientIp(["1.2.3.4, 203.0.113.9"], "fallback"));
    }

    [Fact]
    public void Strips_port_from_the_real_ip()
    {
        Assert.Equal("203.0.113.9", FunctionBase.ExtractClientIp(["203.0.113.9:54321"], "fallback"));
    }

    [Fact]
    public void Keeps_bare_ipv6_intact()
    {
        Assert.Equal("2001:db8::1", FunctionBase.ExtractClientIp(["2001:db8::1"], "fallback"));
    }

    [Fact]
    public void Strips_port_from_bracketed_ipv6()
    {
        Assert.Equal("2001:db8::1", FunctionBase.ExtractClientIp(["[2001:db8::1]:54321"], "fallback"));
    }

    [Fact]
    public void Unwraps_bracketed_ipv6_without_port()
    {
        Assert.Equal("2001:db8::1", FunctionBase.ExtractClientIp(["[2001:db8::1]"], "fallback"));
    }

    [Fact]
    public void Ipv6_key_is_stable_across_changing_source_ports()
    {
        // The source port changes per connection; the brute-force key must not.
        var a = FunctionBase.ExtractClientIp(["[2001:db8::1]:11111"], "fallback");
        var b = FunctionBase.ExtractClientIp(["[2001:db8::1]:22222"], "fallback");
        Assert.Equal(a, b);
        Assert.Equal("2001:db8::1", a);
    }

    [Fact]
    public void Uses_last_across_multiple_header_values()
    {
        Assert.Equal("203.0.113.9", FunctionBase.ExtractClientIp(["1.1.1.1", "2.2.2.2", "203.0.113.9"], "fallback"));
    }

    [Fact]
    public void Falls_back_when_header_absent()
    {
        Assert.Equal("host.example", FunctionBase.ExtractClientIp(null, "host.example"));
    }

    [Fact]
    public void Spoofed_leftmost_cannot_change_the_derived_ip()
    {
        // Different spoofed prefixes, same real appended IP → same brute-force key.
        var a = FunctionBase.ExtractClientIp(["9.9.9.9, 203.0.113.9"], "fallback");
        var b = FunctionBase.ExtractClientIp(["8.8.8.8, 203.0.113.9"], "fallback");
        Assert.Equal(a, b);
        Assert.Equal("203.0.113.9", a);
    }
}
