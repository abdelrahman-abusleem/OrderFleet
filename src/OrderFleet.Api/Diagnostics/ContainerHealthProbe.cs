namespace OrderFleet.Api.Diagnostics;

public static class ContainerHealthProbe
{
    public static async Task<int> ExecuteAsync()
    {
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
            return response.StatusCode == System.Net.HttpStatusCode.OK ? 0 : 1;
        }
        catch { return 1; }
    }
}
