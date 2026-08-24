namespace Fkh.E2ETests;

// Environment-driven configuration for the end-to-end tests. All E2E tests skip
// (rather than fail) when FKH_E2E_BACKEND_URL is not set, so they are inert in CI
// unless explicitly configured.
internal static class E2EConfig
{
    public static string? BackendUrl { get; } = Environment.GetEnvironmentVariable("FKH_E2E_BACKEND_URL")?.TrimEnd('/');

    // Extensive tests provision/mutate real resources; only run when explicitly opted in.
    public static bool Extensive { get; } = string.Equals(Environment.GetEnvironmentVariable("FKH_E2E_EXTENSIVE"), "true", StringComparison.OrdinalIgnoreCase);

    // When true the CLI is invoked with --useOIDC (GitHub Actions). Locally, leave unset
    // and the CLI falls back to GH_TOKEN / `gh auth token`.
    public static bool UseOidc { get; } = string.Equals(Environment.GetEnvironmentVariable("FKH_E2E_USE_OIDC"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(BackendUrl);

    // Web URL is explicit via FKH_E2E_WEB_URL, else inferred from the backend URL
    // (fkh-<org>-backend[/api] -> fkh-<org>-web).
    public static string? WebUrl { get; } = ResolveWebUrl();

    private static string? ResolveWebUrl()
    {
        var explicitUrl = Environment.GetEnvironmentVariable("FKH_E2E_WEB_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.TrimEnd('/');

        var backend = Environment.GetEnvironmentVariable("FKH_E2E_BACKEND_URL")?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(backend))
            return null;

        if (backend.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            backend = backend[..^4];

        return backend.Contains("-backend", StringComparison.OrdinalIgnoreCase)
            ? backend.Replace("-backend", "-web", StringComparison.OrdinalIgnoreCase)
            : null;
    }

    // Unique, self-identifying prefix for any resources created by extensive tests.
    public static string ResourcePrefix { get; } =
        $"e2e{DateTime.UtcNow:MMddHHmmss}";
}
