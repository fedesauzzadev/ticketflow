using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Orders.Api;

/// <summary>
/// Validación de access tokens de Cognito (D-0024): firma contra las claves públicas (JWKS) del
/// user pool, emisor, vencimiento, token_use=access y client_id propio.
/// </summary>
public static class CognitoJwtAuth
{
    public static IServiceCollection AddCognitoJwtAuth(
        this IServiceCollection services, EmulatorAwsOptions aws, string poolId, string clientId)
    {
        var pinned = PinnedCaHttpClientFactory.CreatePinnedHandler(
            X509CertificateLoader.LoadCertificateFromFile(aws.CaPath));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MetadataAddress = $"{aws.Endpoint.TrimEnd('/')}/{poolId}/.well-known/openid-configuration";
            o.BackchannelHttpHandler = new ToEndpointHandler(new Uri(aws.Endpoint)) { InnerHandler = pinned };
            o.BackchannelTimeout = aws.Timeout;
            o.MapInboundClaims = false; // los claims se leen con su nombre original ("sub")
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false, // el access token de Cognito no trae "aud": se valida client_id abajo
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    if (ctx.Principal?.FindFirst("token_use")?.Value != "access"
                        || ctx.Principal.FindFirst("client_id")?.Value != clientId
                        || ctx.Principal.FindFirst("sub") is null)
                        ctx.Fail("Se requiere un access token emitido para este app client.");
                    return Task.CompletedTask;
                },
            };
        });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// El jwks_uri del emulador apunta al host con que se creó el pool; desde un contenedor ese
    /// host no sirve. Todo pedido de claves se redirige al endpoint configurado (y pineado).
    /// </summary>
    private sealed class ToEndpointHandler(Uri endpoint) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!)
            {
                Scheme = endpoint.Scheme, Host = endpoint.Host, Port = endpoint.Port,
            }.Uri;
            return base.SendAsync(request, ct);
        }
    }
}
