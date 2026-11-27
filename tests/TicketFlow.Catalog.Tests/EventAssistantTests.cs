using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>POST /events/{id}/ask contra el emulador (N-4008). El emulador no ejecuta modelos.</summary>
public sealed class EventAssistantTests
{
    private static async Task<string> FirstEventId(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement[]>("/events"))![0].GetProperty("id").GetString()!;

    [SkippableFact]
    public async Task Ask_returns_converse_answer_with_token_usage()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var id = await FirstEventId(client);

        var response = await client.PostAsJsonAsync($"/events/{id}/ask", new { question = "¿Cuánto cuesta?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("event_id").GetString());
        Assert.NotEmpty(body.GetProperty("answer").GetString()!);
        Assert.Equal("us.amazon.nova-micro-v1:0", body.GetProperty("model_id").GetString());
        Assert.Equal("end_turn", body.GetProperty("stop_reason").GetString());
        Assert.True(body.GetProperty("input_tokens").GetInt32() > 0);
        Assert.True(body.GetProperty("output_tokens").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task Ask_rejects_bad_input_without_500()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var id = await FirstEventId(client);

        // Fuzz sembrado: mismos bytes en cada corrida.
        var random = new Random(4008);
        var noise = Enumerable.Range(0, 30).Select(_ =>
            new string(Enumerable.Range(0, random.Next(1, 200)).Select(_ => (char)random.Next(1, 0xD7FF)).ToArray()));
        string[] bodies =
        [
            "", "null", "[]", "{", "\"x\"", "{}", "{\"question\":null}", "{\"question\":\"   \"}",
            "{\"question\":7}", "{\"question\":\"\\ud800\"}",
            JsonSerializer.Serialize(new { question = new string('a', 501) }), .. noise,
        ];
        foreach (var raw in bodies)
        {
            var response = await client.PostAsync($"/events/{id}/ask",
                new StringContent(raw, Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode} para: {raw}");
        }

        var wrongType = await client.PostAsync($"/events/{id}/ask", new StringContent("{\"question\":\"hola\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        var badId = await client.PostAsJsonAsync("/events/BAD_ID/ask", new { question = "hola" });
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var unknown = await client.PostAsJsonAsync("/events/evt-does-not-exist/ask", new { question = "hola" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [SkippableTheory]
    [InlineData("application/json; charset=bogus")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=utf-7")]
    public async Task Ask_ignores_the_declared_charset(string contentType)
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var id = await FirstEventId(client);
        var content = new ByteArrayContent("{\"question\":\"hola\"}"u8.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var response = await client.PostAsync($"/events/{id}/ask", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Ask_returns_503_while_bedrock_is_unreachable_and_the_breaker_opens()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var env = EmulatorAwsOptions.FromEnvironment();
        var down = new EmulatorAwsOptions
        {
            Endpoint = "https://127.0.0.1:1", // nadie escucha
            AccessKeyId = env.AccessKeyId,
            SecretAccessKey = env.SecretAccessKey,
            CaPath = env.CaPath,
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IAmazonBedrockRuntime>(
                EmulatorAwsClients.CreateBedrockRuntime(down))));
        var client = factory.CreateClient();
        var id = await FirstEventId(client);

        for (var i = 0; i < 6; i++) // el breaker abre a los pocos fallos: sigue siendo 503
        {
            var response = await client.PostAsJsonAsync($"/events/{id}/ask", new { question = "hola" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Ask_applies_the_configured_guardrail_and_fails_closed_when_it_is_missing()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var options = EmulatorAwsOptions.FromEnvironment();
        using var bedrock = new AmazonBedrockClient(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonBedrockConfig
            {
                ServiceURL = options.Endpoint,
                AuthenticationRegion = options.Region,
                HttpClientFactory = new PinnedCaHttpClientFactory(
                    X509CertificateLoader.LoadCertificateFromFile(options.CaPath), "bedrock"),
            });
        var id = (await bedrock.CreateGuardrailAsync(new CreateGuardrailRequest
        {
            Name = "it-" + Guid.NewGuid().ToString("N")[..12],
            BlockedInputMessaging = "bloqueado",
            BlockedOutputsMessaging = "bloqueado",
            WordPolicyConfig = new GuardrailWordPolicyConfig { WordsConfig = [new() { Text = "reventa" }] },
        })).GuardrailId;
        using var ssm = EmulatorAwsClients.CreateSsm(options);
        var original = (await ParameterStore.LoadAsync(ssm)).GetValueOrDefault(ParameterNames.GuardrailId);
        try
        {
            await DeleteParameter();
            var blocked = await AskWithGuardrail(id, "¿Dónde hago reventa?");
            Assert.Equal(HttpStatusCode.OK, blocked.Status);
            Assert.Equal("guardrail_intervened", blocked.Body.GetProperty("stop_reason").GetString());
            Assert.Equal("bloqueado", blocked.Body.GetProperty("answer").GetString());

            var allowed = await AskWithGuardrail(id, "¿Cuánto cuesta?");
            Assert.Equal("end_turn", allowed.Body.GetProperty("stop_reason").GetString());

            var unfiltered = await AskWithGuardrail("  ", "¿Dónde hago reventa?"); // sin env ni parámetro
            Assert.Equal("end_turn", unfiltered.Body.GetProperty("stop_reason").GetString());

            await CognitoSetup.PutAsync(ssm, ParameterNames.GuardrailId, id, overwrite: true);
            var fromParameter = await AskWithGuardrail("  ", "¿Dónde hago reventa?");
            Assert.Equal("guardrail_intervened", fromParameter.Body.GetProperty("stop_reason").GetString());

            var envWins = await AskWithGuardrail("noexiste", "¿Cuánto cuesta?");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, envWins.Status);

            await bedrock.DeleteGuardrailAsync(new DeleteGuardrailRequest { GuardrailIdentifier = id });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AskWithGuardrail(id, "hola")).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AskWithGuardrail("  ", "hola")).Status);
        }
        finally
        {
            if (original is null) await DeleteParameter();
            else await CognitoSetup.PutAsync(ssm, ParameterNames.GuardrailId, original, overwrite: true);
            try
            {
                await bedrock.DeleteGuardrailAsync(new DeleteGuardrailRequest { GuardrailIdentifier = id });
            }
            catch (Amazon.Bedrock.Model.ResourceNotFoundException)
            {
            }
        }

        async Task DeleteParameter()
        {
            try
            {
                await ssm.DeleteParameterAsync(new DeleteParameterRequest { Name = ParameterNames.GuardrailId });
            }
            catch (ParameterNotFoundException)
            {
            }
        }
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> AskWithGuardrail(string id, string question)
    {
        Environment.SetEnvironmentVariable("BEDROCK_GUARDRAIL_ID", id);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync($"/events/{await FirstEventId(client)}/ask", new { question });
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BEDROCK_GUARDRAIL_ID", null);
        }
    }

    [Fact]
    public async Task Buffer_handler_cancels_a_stalled_response_body()
    {
        using var client = new HttpMessageInvoker(new BufferResponseHandler { InnerHandler = new StalledBody() });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://localhost/"), timeout.Token));
    }

    private sealed class StalledBody : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage { Content = new NeverEnding() });
    }

    private sealed class NeverEnding : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct);

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [SkippableFact]
    public async Task Ask_returns_503_without_internals_when_bedrock_rejects_the_model()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        // Sin inference profile el emulador (como AWS) responde ValidationException.
        Environment.SetEnvironmentVariable("BEDROCK_MODEL_ID", "amazon.nova-micro-v1:0");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var client = factory.CreateClient();
            var id = await FirstEventId(client);

            var response = await client.PostAsJsonAsync($"/events/{id}/ask", new { question = "hola" });
            var raw = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("assistant-unavailable", raw);
            Assert.DoesNotContain("inference profile", raw);
        }
        finally
        {
            // La env es global al proceso: solo esta clase la usa (xUnit serializa dentro de la clase).
            Environment.SetEnvironmentVariable("BEDROCK_MODEL_ID", null);
        }
    }
}
