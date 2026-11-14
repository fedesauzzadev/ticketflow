using System.Security.Cryptography.X509Certificates;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
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
public sealed class PinnedCaHttpClientFactory(X509Certificate2 ca, string downstream, ILoggerFactory? loggerFactory = null)
    : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
        {
            if (cert is null || chain is null) return false;
            var root = chain.ChainElements[^1].Certificate;
            return string.Equals(root.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase);
        };
        // QA F-01: con factory custom el SDK NO impone ClientConfig.Timeout — el HttpClient
        // debe llevarlo (10 s, NFR-3) o el request cuelga indefinidamente contra un peer congelado.
        var breaker = new CircuitBreakerHandler(downstream, loggerFactory?.CreateLogger("circuit-breaker"));
        breaker.InnerHandler = handler;
        return new HttpClient(breaker) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(10) };
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
                Timeout = options.Timeout,
                MaxErrorRetry = 0, // QA F-01: reintento del SDK multiplica el timeout (4x10 s=40 s medidos); el retry vive en la app.
                HttpClientFactory = pin,
            });
    }
}
