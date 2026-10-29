using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Inventory.Api.Contracts;
using Inventory.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Inventory.Api.Tests;

/// <summary>Sign-up, login and route protection, exercised through the real JWT bearer scheme.</summary>
public class AuthTests(SqlServerDatabase db) : IClassFixture<SqlServerDatabase>
{
    private const string Password = "correct-horse-1";

    private InventoryApiFactory? api;
    private InventoryApiFactory Api => api ??= db.CreateFactory(authenticated: false);

    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private async Task<AuthResponse> RegisterAsync(string email, string name = "Amina Otieno")
    {
        var response = await Api.CreateClient().PostAsJsonAsync("/api/auth/register", new { name, email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Register_returns_a_token_that_unlocks_the_api()
    {
        var email = UniqueEmail();

        var auth = await RegisterAsync(email);

        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal(email, auth.User.Email);
        Assert.True(auth.ExpiresAt > DateTime.UtcNow);
        var products = await ClientWithToken(auth.Token).GetAsync("/api/products?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_email_and_returns_the_account()
    {
        var email = UniqueEmail();
        var registered = await RegisterAsync(email, "Brian Kiptoo");

        var response = await Api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = email.ToUpperInvariant(), password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(registered.User.Id, auth.User.Id);
        var me = await ClientWithToken(auth.Token).GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.Equal(new UserDto(registered.User.Id, "Brian Kiptoo", email), me);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_with_409()
    {
        var email = UniqueEmail();
        await RegisterAsync(email);

        var response = await Api.CreateClient().PostAsJsonAsync("/api/auth/register", new { name = "Someone", email, password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("", "a@example.com", Password)]
    [InlineData("Name", "not-an-email", Password)]
    [InlineData("Name", "a@example.com", "short")]
    public async Task Invalid_registration_is_rejected_with_400(string name, string email, string password)
    {
        var response = await Api.CreateClient().PostAsJsonAsync("/api/auth/register", new { name, email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(await response.ErrorAsync());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        var email = UniqueEmail();
        await RegisterAsync(email);

        var wrongPassword = await Api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password" });
        var unknownEmail = await Api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = UniqueEmail(), password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(await wrongPassword.ErrorAsync(), await unknownEmail.ErrorAsync());
    }

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/orders/recent")]
    [InlineData("/api/auth/me")]
    public async Task Protected_endpoints_require_a_valid_token(string url)
    {
        var anonymous = await Api.CreateClient().GetAsync(url);
        var forged = await ClientWithToken("not-a-real-token").GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Health_stays_public()
    {
        var response = await Api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stock_hub_accepts_a_signed_in_client_and_rejects_anonymous_ones()
    {
        var auth = await RegisterAsync(UniqueEmail());

        await using var signedIn = Connect(() => Task.FromResult<string?>(auth.Token));
        await signedIn.StartAsync();
        await signedIn.InvokeAsync("JoinStockWatchers");
        Assert.Equal(HubConnectionState.Connected, signedIn.State);

        await using var anonymous = Connect(() => Task.FromResult<string?>(null));
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task Stock_hub_reads_the_token_from_the_query_string_like_a_browser_websocket()
    {
        var auth = await RegisterAsync(UniqueEmail());
        var client = Api.CreateClient();

        var withToken = await client.PostAsync($"/hubs/stock/negotiate?negotiateVersion=1&access_token={auth.Token}", null);
        var withoutToken = await client.PostAsync("/hubs/stock/negotiate?negotiateVersion=1", null);
        var tokenOutsideHubs = await client.GetAsync($"/api/products?access_token={auth.Token}");

        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tokenOutsideHubs.StatusCode);
    }

    private HubConnection Connect(Func<Task<string?>> token)
    {
        var server = Api.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/stock"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = token;
            })
            .Build();
    }
}
