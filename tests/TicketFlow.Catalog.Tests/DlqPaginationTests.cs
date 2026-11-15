using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Paginación del peek de DLQ (D-0020, F-V1-R3-01): PeekDlqAsync enumera la DLQ COMPLETA
/// sin borrar (recepciones de a 10 con VisibilityTimeout corto + dedup por MessageId) — el
/// demo de resiliencia debe encontrar SU entrega aunque la DLQ acumule >10 entradas de
/// corridas previas (trigger exacto del QA: peek de UNA página que se pierde la entrega).
/// Cola dedicada por corrida (L-008): determinista.
/// </summary>
public sealed class DlqPaginationTests
{
    private static SagaEnvelope Entry(string purchaseId, int i) => new(
        "probe-step", purchaseId, System.Text.Json.JsonSerializer.SerializeToElement(new { probe = i }));

    [SkippableFact]
    public async Task PeekDlq_enumerates_the_whole_dlq_beyond_one_page_without_deleting()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var queueName = $"it-dlqpage-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var options = EmulatorAwsOptions.FromEnvironment();
        using var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);

        // 15 entradas "sucias" (>1 página de 10): compras distintas => dedup ids distintos.
        var dlq = SqsSagaTransport.DlqQueueOf(queueName);
        await transport.EnsureQueueAsync(dlq, ct);
        var pids = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            var pid = "pur-" + Guid.NewGuid().ToString("N")[..16];
            pids.Add(pid);
            await transport.SendAsync(dlq, Entry(pid, i), ct);
        }

        // Trigger de F-V1-R3-01: con 15 en la DLQ el peek debe ver TODAS, no solo la primera página.
        var peek1 = await transport.PeekDlqAsync(queueName, ct);
        Assert.Equal(15, peek1.Count);
        foreach (var pid in pids)
            Assert.Contains(peek1, e => e.PurchaseId == pid);

        // Peek NO borra: la llamada siguiente ve las mismas 15 (contrato peek-sin-delete).
        Assert.Equal(15, (await transport.PeekDlqAsync(queueName, ct)).Count);
    }

    [SkippableFact]
    public async Task PeekDlq_on_an_empty_dlq_returns_empty()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var queueName = $"it-dlqempty-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var options = EmulatorAwsOptions.FromEnvironment();
        using var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);

        Assert.Empty(await transport.PeekDlqAsync(queueName, ct));
        Assert.Empty(await transport.PeekDlqAsync(queueName, ct));
    }
}
