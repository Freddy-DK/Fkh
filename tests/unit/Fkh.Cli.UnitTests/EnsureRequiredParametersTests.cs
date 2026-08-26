using Fkh.Cli;
using Xunit;

namespace Fkh.Cli.UnitTests;

public class EnsureRequiredParametersTests
{
    private static FunctionParameterDefinition P(string name, string type = "string", bool required = false, string? def = null)
        => new() { Name = name, Type = type, Required = required, DefaultValue = def };

    private static FunctionDefinition Fn(string name, bool requiresConfirmation = false, params FunctionParameterDefinition[] ps)
        => new() { Name = name, Route = name, Parameters = ps.ToList(), RequiresConfirmation = requiresConfirmation };

    private static Dictionary<string, string> Params(params (string Key, string Value)[] kv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Fact]
    public void Throws_when_required_string_parameter_missing()
    {
        var fn = Fn("create", false, P("name", required: true));
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.EnsureRequiredParameters(fn, Params()));
        Assert.Contains("Missing required parameters", ex.Message);
        Assert.Contains("--name", ex.Message);
    }

    [Fact]
    public void Throws_when_required_file_parameter_missing()
    {
        var fn = Fn("upload", false, P("payload", type: "file", required: true));
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.EnsureRequiredParameters(fn, Params()));
        Assert.Contains("Missing required parameters", ex.Message);
        Assert.Contains("--payload", ex.Message);
    }

    [Fact]
    public void Throws_for_unknown_parameter()
    {
        var fn = Fn("run", false, P("name"));
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.EnsureRequiredParameters(fn, Params(("bogus", "x"))));
        Assert.Contains("Unknown parameters", ex.Message);
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Applies_default_for_missing_optional_parameter()
    {
        var fn = Fn("run", false, P("mode", def: "fast"));
        var parameters = Params();
        CliArgs.EnsureRequiredParameters(fn, parameters);
        Assert.Equal("fast", parameters["mode"]);
    }

    [Fact]
    public void Confirm_is_accepted_for_confirmation_required_functions()
    {
        var fn = Fn("remove", requiresConfirmation: true, P("name", required: true));
        var parameters = Params(("name", "x"), ("confirm", "true"));
        // Should not throw: 'confirm' is a reserved known parameter here.
        CliArgs.EnsureRequiredParameters(fn, parameters);
    }

    [Fact]
    public void Auto_detects_ip_when_delegate_supplied()
    {
        var fn = Fn("allow", false, P("ip"));
        var parameters = Params();
        CliArgs.EnsureRequiredParameters(fn, parameters, detectPublicIp: () => "1.2.3.4");
        Assert.Equal("1.2.3.4", parameters["ip"]);
    }

    [Fact]
    public void Does_not_set_ip_when_no_delegate_supplied()
    {
        var fn = Fn("allow", false, P("ip"));
        var parameters = Params();
        CliArgs.EnsureRequiredParameters(fn, parameters, detectPublicIp: null);
        Assert.False(parameters.ContainsKey("ip"));
    }
}
