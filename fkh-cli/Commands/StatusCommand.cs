using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

sealed class StatusCommand : ClientCommand
{
    public override string Name => "Status";
    public override string Description => "Returns system status including Kubernetes nodes, BC containers, SQL, storage, quotas, and security. Admin only.";
    public override List<ClientCommandParameter> Parameters => [];

    public override async Task<int> ExecuteAsync(string[] args, CliSettings settings, bool asJson)
    {
        Dictionary<string, string> parameters;
        try
        {
            parameters = ParseClientArgs(args);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{Ansi.Red}{ex.Message}{Ansi.Reset}");
            return 1;
        }

        var token = CreateTokenProvider(parameters, settings).GetToken();
        var backendUrl = ValidateBackendUrl(settings.BackendUrl);
        if (backendUrl is null)
            return 1;

        if (!asJson)
            Console.WriteLine($"{Ansi.Dim}Fetching system status...{Ansi.Reset}");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/Status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddProtocolHeaders(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new FunctionInvokeRequest
            {
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }),
            Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{Ansi.Red}Status request failed ({(int)response.StatusCode}): {body}{Ansi.Reset}");
            return 1;
        }

        if (asJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                Console.WriteLine(body);
            }
        }
        else
        {
            // Pretty-print the status JSON using indented JSON since FormatJsonAsText is a top-level local function
            try
            {
                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                Console.WriteLine(body);
            }
        }

        return 0;
    }
}
