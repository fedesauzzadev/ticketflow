using System.Net.Http.Headers;
using System.Text.Json;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Orders.Tests;

/// <summary>Login real contra el Cognito del emulador (D-0024): sin atajos ni esquemas falsos.</summary>
public static class AuthTestSupport
{
    private static readonly Dictionary<string, AuthenticationResultType> Sessions = [];

    public static AuthenticationResultType SignIn(string user = "it-user")
    {
        lock (Sessions)
        {
            if (Sessions.TryGetValue(user, out var cached)) return cached;
            var options = EmulatorAwsOptions.FromEnvironment();
            using var cognito = EmulatorAwsClients.CreateCognito(options);
            using var ssm = EmulatorAwsClients.CreateSsm(options);
            return Sessions[user] = CognitoSetup.SignInDemoUserAsync(cognito, ssm, user).GetAwaiter().GetResult();
        }
    }

    public static string Sub(string user = "it-user")
    {
        var payload = WebEncoders.Base64UrlDecode(SignIn(user).AccessToken.Split('.')[1]);
        return JsonDocument.Parse(payload).RootElement.GetProperty("sub").GetString()!;
    }

    /// <summary>Firma ANTES de levantar el host: Órdenes necesita el pool publicado al arrancar.</summary>
    public static HttpClient CreateAuthClient(this WebApplicationFactory<Program> factory, string user = "it-user")
    {
        var token = SignIn(user).AccessToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
