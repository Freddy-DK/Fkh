using Fkh.Cli;
using Xunit;

namespace Fkh.Cli.UnitTests;

public class ParseArgsTests
{
    private static FunctionParameterDefinition P(string name, string type = "string", bool required = false, string? def = null)
        => new() { Name = name, Type = type, Required = required, DefaultValue = def };

    private static FunctionDefinition Fn(string name, bool requiresConfirmation = false, params FunctionParameterDefinition[] ps)
        => new() { Name = name, Route = name, Parameters = ps.ToList(), RequiresConfirmation = requiresConfirmation };

    private static FunctionCatalogResponse Catalog(params FunctionDefinition[] fns)
        => new() { Functions = fns.ToList() };

    [Fact]
    public void Returns_help_for_no_args_or_help_flags()
    {
        var catalog = Catalog(Fn("listcontainers"));
        Assert.True(CliArgs.ParseArgs([], catalog).ShowHelp);
        Assert.True(CliArgs.ParseArgs(["-h"], catalog).ShowHelp);
        Assert.True(CliArgs.ParseArgs(["--help"], catalog).ShowHelp);
    }

    [Fact]
    public void Resolves_command_name_case_insensitively_to_canonical_name()
    {
        var parsed = CliArgs.ParseArgs(["listcontainers"], Catalog(Fn("ListContainers")));
        Assert.Equal("ListContainers", parsed.Command);
    }

    [Fact]
    public void Throws_for_unsupported_command()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.ParseArgs(["bogus"], Catalog(Fn("run"))));
        Assert.Contains("Unsupported command", ex.Message);
    }

    [Fact]
    public void Throws_for_positional_argument_after_command()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.ParseArgs(["run", "oops"], Catalog(Fn("run"))));
        Assert.Contains("Unknown argument", ex.Message);
    }

    [Fact]
    public void Throws_for_empty_parameter_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.ParseArgs(["run", "--"], Catalog(Fn("run"))));
        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public void Throws_when_backendUrl_value_is_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.ParseArgs(["run", "--backendUrl"], Catalog(Fn("run"))));
        Assert.Contains("Missing value for --backendUrl", ex.Message);
    }

    [Fact]
    public void Throws_when_output_value_is_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CliArgs.ParseArgs(["run", "--output"], Catalog(Fn("run"))));
        Assert.Contains("Missing value for --output", ex.Message);
    }

    [Fact]
    public void Reserved_flags_are_consumed_and_not_treated_as_parameters()
    {
        var parsed = CliArgs.ParseArgs(
            ["run", "--nowait", "--asJson", "--open", "--useOIDC", "--ghUser", "me", "--backendUrl", "https://x/api", "--output", "out.txt"],
            Catalog(Fn("run")));

        Assert.True(parsed.NoWait);
        Assert.True(parsed.AsJson);
        Assert.True(parsed.Open);
        Assert.Equal("out.txt", parsed.Output);
        Assert.Empty(parsed.Parameters);
    }

    [Fact]
    public void Value_parameter_captures_following_token()
    {
        var parsed = CliArgs.ParseArgs(["run", "--name", "mybc"], Catalog(Fn("run", false, P("name"))));
        Assert.Equal("mybc", parsed.Parameters["name"]);
    }

    [Fact]
    public void Catalog_boolean_parameter_is_a_flag()
    {
        var parsed = CliArgs.ParseArgs(["run", "--force"], Catalog(Fn("run", false, P("force", type: "boolean"))));
        Assert.Equal("true", parsed.Parameters["force"]);
    }

    [Fact]
    public void Parameter_followed_by_another_flag_is_treated_as_boolean()
    {
        // A value parameter with no following value (next token is another flag) becomes "true".
        var parsed = CliArgs.ParseArgs(["run", "--name", "--nowait"], Catalog(Fn("run", false, P("name"))));
        Assert.Equal("true", parsed.Parameters["name"]);
        Assert.True(parsed.NoWait);
    }

    [Fact]
    public void Trailing_value_parameter_without_value_is_treated_as_boolean()
    {
        var parsed = CliArgs.ParseArgs(["run", "--name"], Catalog(Fn("run", false, P("name"))));
        Assert.Equal("true", parsed.Parameters["name"]);
    }
}
