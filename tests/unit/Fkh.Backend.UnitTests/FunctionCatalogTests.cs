using Fkh;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class FunctionCatalogTests
{
    private static readonly string[] AllowedTypes = ["string", "boolean", "file"];

    [Fact]
    public void Catalog_is_not_empty()
    {
        Assert.NotEmpty(FunctionCatalog.Functions);
    }

    [Fact]
    public void Function_names_are_unique_case_insensitive()
    {
        var duplicates = FunctionCatalog.Functions
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Function_routes_are_unique_case_insensitive()
    {
        var duplicates = FunctionCatalog.Functions
            .GroupBy(f => f.Route, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Functions_have_name_route_and_description()
    {
        foreach (var f in FunctionCatalog.Functions)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Name));
            Assert.False(string.IsNullOrWhiteSpace(f.Route));
            Assert.False(string.IsNullOrWhiteSpace(f.Description));
        }
    }

    [Fact]
    public void Parameters_are_well_formed()
    {
        foreach (var f in FunctionCatalog.Functions)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in f.Parameters)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name), $"{f.Name} has a parameter with no name.");
                Assert.Contains(p.Type, AllowedTypes);
                Assert.True(names.Add(p.Name), $"{f.Name} has duplicate parameter '{p.Name}'.");
            }
        }
    }

    [Fact]
    public void Boolean_default_values_parse()
    {
        foreach (var f in FunctionCatalog.Functions)
        {
            foreach (var p in f.Parameters.Where(p => p.Type == "boolean" && p.DefaultValue is not null))
            {
                Assert.True(bool.TryParse(p.DefaultValue, out _), $"{f.Name}.{p.Name} has non-boolean default '{p.DefaultValue}'.");
            }
        }
    }
}
