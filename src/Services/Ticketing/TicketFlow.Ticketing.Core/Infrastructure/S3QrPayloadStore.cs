using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TicketFlow.Ticketing.Core.Application;

namespace TicketFlow.Ticketing.Core.Infrastructure;

/// <summary>
/// Payload QR de cada entrada en S3 del emulador (D-0005): bucket ticketflow-tickets, key
/// qr/&lt;purchase_id&gt;/&lt;ticket_id&gt;.json — referenciable desde GET /tickets/{id} (H-02 AC1).
/// </summary>
public sealed class S3QrPayloadStore(IAmazonS3 s3) : IQrPayloadStore
{
    public const string BucketName = "ticketflow-tickets";

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Creación idempotente, tolerante a carreras de arranque (el emulador da 500 crudos).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = BucketName }, ct);
                return;
            }
            catch (BucketAlreadyOwnedByYouException)
            {
                return; // ya existe de una corrida anterior
            }
            catch (AmazonServiceException)
            {
                await Task.Delay(250, ct); // carrera/transient: reintentar
            }
        }
        throw new TimeoutException($"El bucket {BucketName} no quedó disponible en ~2,5 s.");
    }

    public async Task PutAsync(string key, string payloadJson, CancellationToken ct) =>
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            ContentBody = payloadJson,
            ContentType = "application/json",
        }, ct);

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = BucketName, Key = key }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (NoSuchKeyException)
        {
            return null;
        }
    }
}
