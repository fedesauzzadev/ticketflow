using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TicketFlow.Orders.Tests;

/// <summary>Autenticación y propiedad de las compras con Cognito (D-0024).</summary>
public sealed class AuthApiTests
{
    private const string Unknown = "/purchases/pur-00000000000000000000000000000000";

    private static HttpContent Body() => new StringContent(
        """{"event_id":"evt-api-test","seats":1,"card":{"pan":"4111111111111111","holder":"Fan","expiry":"12/29","cvv":"123"}}""",
        Encoding.UTF8, "application/json");

    [SkippableTheory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("id-token")]
    [InlineData("tampered")]
    public async Task Purchase_endpoints_reject_anything_but_a_valid_access_token(string kind)
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var session = AuthTestSupport.SignIn();
        var token = session.AccessToken;
        var signature = token.LastIndexOf('.') + 1;
        var bearer = kind switch
        {
            "garbage" => "no-es-un-jwt",
            "id-token" => session.IdToken,
            "tampered" => token[..signature] + (token[signature] == 'A' ? 'B' : 'A') + token[(signature + 1)..],
            _ => null,
        };
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "auth-" + Guid.NewGuid().ToString("N"));

        using var body = Body();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/purchases", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Unknown + "/payment", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [SkippableFact]
    public async Task Another_user_cannot_read_pay_or_replay_my_purchase()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = new WebApplicationFactory<Program>();
        var mine = factory.CreateAuthClient("it-user");
        var other = factory.CreateAuthClient("it-other");
        var key = "auth-" + Guid.NewGuid().ToString("N");
        mine.DefaultRequestHeaders.Add("Idempotency-Key", key);
        other.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var first = Body();
        var created = await mine.PostAsync("/purchases", first);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = created.Headers.Location!.ToString();

        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(url + "/payment", null)).StatusCode);

        // Misma key y mismo body, otro usuario: compra NUEVA, no un replay de la mía.
        using var second = Body();
        var theirs = await other.PostAsync("/purchases", second);
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.NotEqual(url, theirs.Headers.Location!.ToString());
        Assert.DoesNotContain("owner", await theirs.Content.ReadAsStringAsync()); // el sub no sale en la API
    }
}
