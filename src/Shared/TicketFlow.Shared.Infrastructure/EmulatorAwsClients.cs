using System.Security.Cryptography.X509Certificates;
using Amazon.Bedrock;
using Amazon.BedrockRuntime;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Amazon.SQS;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// HttpClientFactory del SDK con pin del root CA del emulador (D-0003, hallazgo L-001):
/// el root de la cadena servida DEBE ser exactamente la CA pineada. Un endpoint que no
/// presente esa cadena (p. ej. AWS real, por mala config) es rechazado — el gate de
/// división "cero llamadas a nube real" se cumple por diseño, no por convención.
/// Envuelve el handler con <see cref="CircuitBreakerHandler"/> (D-0012): un breaker por
/// downstream (una instancia de HttpClient por cliente del SDK).
/// </summary>
public sealed class PinnedCaHttpClientFactory(X509Certificate2 ca, string downstream,
    ILoggerFactory? loggerFactory = null, bool bufferResponse = false)
    : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        var handler = CreatePinnedHandler(ca);
        // QA F-01: con factory custom el SDK NO impone ClientConfig.Timeout — el HttpClient
        // debe llevarlo (10 s, NFR-3) o el request cuelga indefinidamente contra un peer congelado.
        var breaker = new CircuitBreakerHandler(downstream, loggerFactory?.CreateLogger("circuit-breaker"));
        breaker.InnerHandler = bufferResponse ? new BufferResponseHandler { InnerHandler = handler } : handler;
        return new HttpClient(breaker) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>Handler que solo acepta cadenas cuyo root es la CA del emulador.</summary>
    public static HttpClientHandler CreatePinnedHandler(X509Certificate2 ca) => new()
    {
        ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
        {
            if (cert is null || chain is null) return false;
            var root = chain.ChainElements[^1].Certificate;
            return string.Equals(root.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase);
        },
    };
}

/// <summary>
/// Trae el body completo de forma asíncrona y cancelable antes de que el SDK lo lea (N-4008): Bedrock
/// lo lee de forma síncrona y unas pocas respuestas colgadas agotaban el thread pool.
/// No usar con APIs de streaming.
/// </summary>
public sealed class BufferResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        await response.Content.LoadIntoBufferAsync(ct);
        return response;
    }
}

public static class EmulatorAwsClients
{
    /// <summary>
    /// Cliente DynamoDB pre-configurado para el emulador. REGLA VERIFICADA (L-001):
    /// ServiceURL + AuthenticationRegion, NUNCA RegionEndpoint — en SDK v4 RegionEndpoint
    /// gana silenciosamente y el tráfico sale a AWS real.
    /// </summary>
    public static AmazonDynamoDBClient CreateDynamoDb(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "dynamodb", loggerFactory);
        return new AmazonDynamoDBClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonDynamoDBConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0, // QA F-01: reintento del SDK multiplica el timeout (4x10 s=40 s medidos); el retry vive en la app (redelivery del worker, loops L-007).
                HttpClientFactory = pin,
            });
    }

    /// <summary>Cliente SQS del emulador (transporte de la saga, D-0006). Misma regla que DynamoDB.</summary>
    public static AmazonSQSClient CreateSqs(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "sqs", loggerFactory);
        return new AmazonSQSClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonSQSConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0, // QA F-01: reintento del SDK multiplica el timeout (4x10 s=40 s medidos); el retry vive en la app.
                HttpClientFactory = pin,
            });
    }

    /// <summary>Cliente S3 del emulador (payload QR de las entradas, D-0005). Misma regla que DynamoDB.</summary>
    public static AmazonS3Client CreateS3(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "s3", loggerFactory);
        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                // D-0023: con un host por NOMBRE (contenedores) el SDK usaría virtual-hosted style
                // (bucket.host → DNS inexistente); con IP ya usaba path-style implícito.
                ForcePathStyle = true,
                Timeout = options.Timeout,
                MaxErrorRetry = 0, // QA F-01: reintento del SDK multiplica el timeout (4x10 s=40 s medidos); el retry vive en la app.
                HttpClientFactory = pin,
            });
    }

    /// <summary>Cliente Ssm del emulador (D-0024). Misma regla que DynamoDB.</summary>
    public static AmazonSimpleSystemsManagementClient CreateSsm(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "ssm", loggerFactory);
        return new AmazonSimpleSystemsManagementClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonSimpleSystemsManagementConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0,
                HttpClientFactory = pin,
            });
    }

    /// <summary>Cliente Bedrock Runtime del emulador (N-4008). Misma regla que DynamoDB.</summary>
    public static AmazonBedrockRuntimeClient CreateBedrockRuntime(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "bedrock-runtime", loggerFactory, bufferResponse: true);
        var client = new AmazonBedrockRuntimeClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonBedrockRuntimeConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0,
                HttpClientFactory = pin,
            });
        // El constructor antepone Bearer si el entorno trae AWS_BEARER_TOKEN_BEDROCK (una key real
        // viajaría al emulador): se fuerza SigV4 DESPUÉS de construirlo.
        var config = (AmazonBedrockRuntimeConfig)client.Config;
        config.AuthSchemePreference = ["sigv4"];
        config.AWSTokenProvider = null;
        return client;
    }

    /// <summary>Cliente Bedrock (plano de control: guardrails, N-4010). Misma regla que Bedrock Runtime.</summary>
    public static AmazonBedrockClient CreateBedrock(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "bedrock", loggerFactory);
        var client = new AmazonBedrockClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonBedrockConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0,
                HttpClientFactory = pin,
            });
        var config = (AmazonBedrockConfig)client.Config;
        config.AuthSchemePreference = ["sigv4"]; // nunca Bearer, ver CreateBedrockRuntime
        config.AWSTokenProvider = null;
        return client;
    }

    /// <summary>Cliente Cognito del emulador (D-0024). Misma regla que DynamoDB.</summary>
    public static AmazonCognitoIdentityProviderClient CreateCognito(EmulatorAwsOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pin = new PinnedCaHttpClientFactory(X509CertificateLoader.LoadCertificateFromFile(options.CaPath),
            "cognito-idp", loggerFactory);
        return new AmazonCognitoIdentityProviderClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonCognitoIdentityProviderConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                Timeout = options.Timeout,
                MaxErrorRetry = 0,
                HttpClientFactory = pin,
            });
    }
}
