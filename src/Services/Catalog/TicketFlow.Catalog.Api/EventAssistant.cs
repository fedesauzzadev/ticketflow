using System.Globalization;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Api;

/// <summary>
/// Asistente de eventos sobre la API Converse de Bedrock (N-4008). Los datos del evento van en el
/// system prompt y la pregunta del usuario como mensaje: nunca se mezclan.
/// </summary>
public sealed class EventAssistant(IAmazonBedrockRuntime bedrock, string modelId)
{
    // On-demand exige un inference profile (prefijo us.), no el id del modelo suelto.
    public const string DefaultModelId = "us.amazon.nova-micro-v1:0";
    public const int MaxQuestionLength = 500;

    public async Task<AskResponse> AskAsync(CatalogEvent evt, string question, CancellationToken ct)
    {
        var response = await bedrock.ConverseAsync(new ConverseRequest
        {
            ModelId = modelId,
            System = [new SystemContentBlock { Text = SystemPrompt(evt) }],
            Messages = [new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = question }] }],
            InferenceConfig = new InferenceConfiguration { MaxTokens = 300, Temperature = 0.2f },
        }, ct);

        var answer = string.Concat(response.Output?.Message?.Content?.Select(c => c.Text) ?? []);
        return new AskResponse(evt.Id, answer, modelId, response.StopReason?.Value ?? "",
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0);
    }

    private static string SystemPrompt(CatalogEvent evt) => string.Create(CultureInfo.InvariantCulture,
        $"""
        Respondé preguntas sobre un evento de TicketFlow, breve y solo con estos datos.
        Si el dato no está, decí que no lo sabés.
        Nombre: {evt.Name}
        Fecha: {evt.Date:O}
        Precio: {evt.Price}
        Entradas disponibles: {evt.Available}
        """);
}

public sealed record AskRequest(string? Question);

public sealed record AskResponse(
    string EventId, string Answer, string ModelId, string StopReason, int InputTokens, int OutputTokens);
