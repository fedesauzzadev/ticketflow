using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amazon.DynamoDBv2;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// QA F-01 / NFR-3: el HttpClient del PinnedCaHttpClientFactory DEBE acotar cada request
/// (ClientConfig.Timeout). Con Timeout.InfiniteTimeSpan (código original) un peer colgado
/// (emulador pausado) dejaba el request suspendido 150-240 s+ en vez de fallar a los 10 s.
/// </summary>
public class PinnedCaHttpClientFactoryTests
{
    [Fact]
    public void CreateHttpClient_lleva_el_timeout_del_client_config()
    {
        using var ca = SelfSigned();
        var factory = new PinnedCaHttpClientFactory(ca);
        var config = new AmazonDynamoDBConfig { Timeout = TimeSpan.FromSeconds(10) };

        using var http = factory.CreateHttpClient(config);

        Assert.Equal(TimeSpan.FromSeconds(10), http.Timeout);
    }

    [Fact]
    public async Task Request_a_peer_colgado_falla_a_los_10s_no_queda_colgado()
    {
        // Servidor que acepta la conexión y NUNCA responde: el handshake TLS cuelga para
        // siempre — equivalente local del emulador con docker pause (evidencia QA F-01).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        var accepting = Task.Run(() =>
        {
            try { while (true) accepted.Add(listener.AcceptTcpClient()); }
            catch (Exception) { /* listener detenido al final del test */ }
        });
        try
        {
            using var ca = SelfSigned();
            var factory = new PinnedCaHttpClientFactory(ca);
            var config = new AmazonDynamoDBConfig { Timeout = TimeSpan.FromSeconds(10) };
            using var http = factory.CreateHttpClient(config);

            // Watchdog: si el timeout del HttpClient no dispara, este token corta a los 20 s
            // y el assert de abajo (watchdog NO cancelado) convierte el hang en fallo del test.
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            var sw = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<TaskCanceledException>(
                () => http.GetAsync($"https://127.0.0.1:{port}/", watchdog.Token));

            sw.Stop();
            Assert.False(watchdog.IsCancellationRequested,
                "el corte vino del watchdog (20 s), no del timeout del HttpClient — timeout NO efectivo");
            Assert.InRange(sw.Elapsed.TotalSeconds, 9, 11.5); // 10 s configurados, no un fallo instantáneo
        }
        finally
        {
            listener.Stop();
            foreach (var c in accepted) c.Dispose();
            await accepting;
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ticketflow-test-ca", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
