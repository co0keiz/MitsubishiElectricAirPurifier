using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirPurifier.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirPurifier.Tests;

public sealed class ApiTests
{
    private static WebApplicationFactory<Program> CreateApi(ScriptedDevice device, string? apiKey = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (apiKey is not null) builder.UseSetting("Api:ApiKey", apiKey);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton(new HttpClient(device));
                services.PostConfigure<PurifierOptions>(options => options.ReadbackDelay = TimeSpan.Zero);
            });
        });

    [Fact]
    public async Task StatusReturnsFreshConfirmedStateWithModeName()
    {
        using var device = new ScriptedDevice(Fixtures.Turbo);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.GetAsync("/api/purifier/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turbo", json.GetProperty("autoMode").GetString());
        Assert.True(json.GetProperty("isOn").GetBoolean());
        Assert.True(json.GetProperty("confirmed").GetBoolean());
        Assert.Single(device.Requests);
    }

    [Fact]
    public async Task PowerAndModeEndpointsInvokeNativeVerifiedOperations()
    {
        using var device = new ScriptedDevice(Fixtures.Off, Fixtures.Off, Fixtures.Normal,
            Fixtures.Normal, Fixtures.Normal, Fixtures.Night);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var power = await http.PutAsJsonAsync("/api/purifier/power", new { on = true });
        Assert.Equal(HttpStatusCode.OK, power.StatusCode);
        Assert.True((await power.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isOn").GetBoolean());
        using var mode = await http.PutAsJsonAsync("/api/purifier/auto-mode", new { mode = "night" });
        Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
        Assert.Equal("night", (await mode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoMode").GetString());
        Assert.Equal(6, device.Requests.Count);
    }

    [Theory]
    [InlineData("power", "{}")]
    [InlineData("power", "{\"on\":null}")]
    [InlineData("power", "{\"on\":\"yes\"}")]
    [InlineData("auto-mode", "{}")]
    [InlineData("auto-mode", "{\"mode\":null}")]
    [InlineData("auto-mode", "{\"mode\":\"invalid\"}")]
    [InlineData("auto-mode", "{\"mode\":0}")]
    [InlineData("auto-mode", "{")]
    [InlineData("auto-mode", "null")]
    [InlineData("manual-speed", "{}")]
    [InlineData("manual-speed", "{\"speed\":null}")]
    [InlineData("manual-speed", "{\"speed\":\"invalid\"}")]
    [InlineData("manual-speed", "{\"speed\":0}")]
    [InlineData("manual-speed", "null")]
    [InlineData("smart-search", "{}")]
    [InlineData("smart-search", "null")]
    [InlineData("smart-search", "{\"enabled\":null}")]
    [InlineData("smart-search", "{\"enabled\":\"on\"}")]
    [InlineData("smart-search", "{\"enabled\":0}")]
    public async Task RejectsInvalidInputWithoutContactingPurifier(string path, string payload)
    {
        using var device = new ScriptedDevice();
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PutAsync("/api/purifier/" + path, new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(device.Requests);
    }

    [Fact]
    public async Task OffDeviceReturnsConflictInsteadOfImplicitlyPoweringOn()
    {
        using var device = new ScriptedDevice(Fixtures.Off);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PutAsJsonAsync("/api/purifier/auto-mode", new { mode = "night" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DeviceOff", json.GetProperty("code").GetString());
        Assert.False(json.GetProperty("confirmed").GetBoolean());
        Assert.False(json.GetProperty("commandMayHaveSucceeded").GetBoolean());
        Assert.Single(device.Requests);
    }

    [Fact]
    public async Task UnconfirmedCommandReturnsGatewayTimeoutAndExplicitAmbiguousResult()
    {
        using var device = new ScriptedDevice(Fixtures.Normal);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PutAsJsonAsync("/api/purifier/auto-mode", new { mode = "night" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("confirmed").GetBoolean());
        Assert.True(json.GetProperty("commandMayHaveSucceeded").GetBoolean());
        Assert.Equal("CommandUnconfirmed", json.GetProperty("code").GetString());
        Assert.Equal(5, device.Requests.Count);
    }

    [Fact]
    public async Task ConnectionFailureAndMalformedResponseHaveDistinctErrors()
    {
        using var unavailable = new ScriptedDevice { Respond = (_, _, _) => throw new HttpRequestException("Offline") };
        await using var unavailableApi = CreateApi(unavailable);
        using var unavailableHttp = unavailableApi.CreateClient();
        using var failure = await unavailableHttp.GetAsync("/api/purifier/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);

        using var malformed = new ScriptedDevice { Respond = (_, _, _) => Task.FromResult("<LSV/>") };
        await using var malformedApi = CreateApi(malformed);
        using var malformedHttp = malformedApi.CreateClient();
        using var rejected = await malformedHttp.GetAsync("/api/purifier/status");
        Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
    }

    [Fact]
    public async Task OptionalApiKeyProtectsDeviceEndpoints()
    {
        using var device = new ScriptedDevice();
        await using var api = CreateApi(device, "test-key");
        using var http = api.CreateClient();
        using var rejected = await http.GetAsync("/api/purifier/status");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Empty(device.Requests);
        http.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
        using var accepted = await http.GetAsync("/api/purifier/status");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task HealthAndApiDocumentationDoNotContactPurifier()
    {
        using var device = new ScriptedDevice();
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var health = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var response = await http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("paths").TryGetProperty("/api/purifier/auto-mode", out _));
        Assert.True(json.GetProperty("paths").TryGetProperty("/api/purifier/auto-cleaning", out _));
        Assert.True(json.GetProperty("paths").TryGetProperty("/api/purifier/smart-search", out _));
        Assert.Equal("Mitsubishi Electric Air Purifier API", json.GetProperty("info").GetProperty("title").GetString());
        Assert.False(json.GetProperty("paths").GetProperty("/api/purifier/status").GetProperty("get").TryGetProperty("security", out _));
        using var scalar = await http.GetAsync("/scalar/v1");
        Assert.Equal(HttpStatusCode.OK, scalar.StatusCode);
        Assert.Equal("text/html", scalar.Content.Headers.ContentType!.MediaType);
        var html = await scalar.Content.ReadAsStringAsync();
        Assert.Contains("Mitsubishi Electric Air Purifier API", html);
        Assert.Contains("\"url\":\"openapi/v1.json\"", html);
        using var scalarScript = await http.GetAsync("/scalar/scalar.js");
        Assert.Equal(HttpStatusCode.OK, scalarScript.StatusCode);
        using var integrationScript = await http.GetAsync("/scalar/scalar.aspnetcore.js");
        Assert.Equal(HttpStatusCode.OK, integrationScript.StatusCode);
        Assert.Empty(device.Requests);
    }

    [Fact]
    public async Task OpenApiDescribesConfiguredApiKeyWithoutExposingItsValue()
    {
        using var device = new ScriptedDevice();
        await using var api = CreateApi(device, "test-key");
        using var http = api.CreateClient();
        using var response = await http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scheme = json.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        foreach (var (path, method) in new[]
                 {
                     ("/api/purifier/status", "get"), ("/api/purifier/power", "put"),
                     ("/api/purifier/auto-mode", "put"), ("/api/purifier/manual-speed", "put"),
                     ("/api/purifier/auto-cleaning", "post"), ("/api/purifier/smart-search", "put")
                 })
        {
            Assert.Equal(JsonValueKind.Array,
                json.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("security")[0].GetProperty("ApiKey").ValueKind);
        }
        Assert.False(json.GetProperty("paths").GetProperty("/health").GetProperty("get").TryGetProperty("security", out _));
        Assert.DoesNotContain("test-key", json.GetRawText());
        Assert.Empty(device.Requests);
    }

    [Theory]
    [InlineData("silent", Fixtures.ManualSilent)]
    [InlineData("low", Fixtures.ManualLow)]
    [InlineData("mid", Fixtures.ManualMid)]
    [InlineData("high", Fixtures.ManualHigh)]
    [InlineData("turbo", Fixtures.ManualTurbo)]
    public async Task ManualSpeedEndpointReportsManualOperation(string speed, string target)
    {
        using var device = new ScriptedDevice(Fixtures.Normal, Fixtures.Normal, target);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PutAsJsonAsync("/api/purifier/manual-speed", new { speed });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual", json.GetProperty("operatingMode").GetString());
        Assert.Equal(speed, json.GetProperty("manualSpeed").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("autoMode").ValueKind);
        Assert.True(json.GetProperty("confirmed").GetBoolean());
        Assert.Equal(3, device.Requests.Count);
    }

    [Fact]
    public async Task AutoCleaningEndpointStartsTheCycleAndReturnsConfirmedActiveState()
    {
        using var device = new ScriptedDevice(Fixtures.Normal, Fixtures.Normal, Fixtures.AutoCleaningActive);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PostAsync("/api/purifier/auto-cleaning", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isAutoCleaning").GetBoolean());
        Assert.True(json.GetProperty("confirmed").GetBoolean());
        Assert.Equal("normal", json.GetProperty("autoMode").GetString());
        Assert.Equal("auto", json.GetProperty("operatingMode").GetString());
        Assert.Equal(3, device.Requests.Count);
    }

    [Theory]
    [InlineData(true, Fixtures.SmartSearchOff, Fixtures.Normal)]
    [InlineData(false, Fixtures.Normal, Fixtures.SmartSearchOff)]
    public async Task SmartSearchEndpointAcceptsBothBooleanStates(bool enabled, string before, string after)
    {
        using var device = new ScriptedDevice(before, before, after);
        await using var api = CreateApi(device);
        using var http = api.CreateClient();
        using var response = await http.PutAsJsonAsync("/api/purifier/smart-search", new { enabled });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(enabled, json.GetProperty("isSmartSearchEnabled").GetBoolean());
        Assert.Equal("normal", json.GetProperty("autoMode").GetString());
        Assert.True(json.GetProperty("confirmed").GetBoolean());
        Assert.Equal(3, device.Requests.Count);
    }
}
