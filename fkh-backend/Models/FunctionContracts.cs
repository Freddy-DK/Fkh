namespace Fkh.Models;

public sealed class FunctionParameterDefinition
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
    public bool AdminOnly { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed class FunctionDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Route { get; init; }
    public required List<FunctionParameterDefinition> Parameters { get; init; }

    /// <summary>
    /// When true, this function is excluded from the public catalog response
    /// but can still be invoked directly by clients that know the route.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// When true, only admin-team members may invoke this function.
    /// OIDC callers are also rejected.
    /// </summary>
    public bool AdminOnly { get; init; }

    /// <summary>
    /// When true, clients should prompt for confirmation before invoking.
    /// The function expects a "confirm" boolean parameter set to true.
    /// </summary>
    public bool RequiresConfirmation { get; init; }
}

public sealed class FunctionCatalogResponse
{
    public required List<FunctionDefinition> Functions { get; init; }
}

public sealed class FunctionInvokeRequest
{
    public Dictionary<string, string>? Parameters { get; init; }
}

/// <summary>
/// Throw from a service operation to signal the caller should retry after a delay.
/// FunctionBase catches this and returns HTTP 202 with a Retry-After header.
/// </summary>
public sealed class RetryAfterException : Exception
{
    public int RetryAfterSeconds { get; }

    public RetryAfterException(string message, int retryAfterSeconds)
        : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}
