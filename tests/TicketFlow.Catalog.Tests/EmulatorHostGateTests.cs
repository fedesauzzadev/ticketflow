using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// Gate NFR-7 (D-0023): loopback siempre; un host extra solo por opt-in explícito; AWS real nunca.
public class EmulatorHostGateTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void Loopback_siempre_permitido(string host) =>
        Assert.True(EmulatorAwsOptions.IsAllowedHost(host, null));

    [Theory]
    [InlineData("aws-emulator", null)]                          // sin opt-in: comportamiento histórico
    [InlineData("aws-emulator", "")]
    [InlineData("aws-emulator", "  ")]
    [InlineData("otro-host", "aws-emulator")]                   // el opt-in autoriza UN host exacto
    [InlineData("aws-emulator.evil.com", "aws-emulator")]       // sin match por prefijo
    [InlineData("xaws-emulator", "aws-emulator")]               // ni por sufijo
    [InlineData("dynamodb.us-east-1.amazonaws.com", null)]
    [InlineData("dynamodb.us-east-1.amazonaws.com", "dynamodb.us-east-1.amazonaws.com")] // AWS real: jamás
    [InlineData("s3.AMAZONAWS.com", "s3.AMAZONAWS.com")]
    [InlineData("console.aws.com", "console.aws.com")]
    [InlineData("", "")]
    public void Rechazado(string host, string? allowed) =>
        Assert.False(EmulatorAwsOptions.IsAllowedHost(host, allowed));

    [Theory]
    [InlineData("aws-emulator", "aws-emulator")]
    [InlineData("host.docker.internal", "host.docker.internal")]
    [InlineData("AWS-Emulator", " aws-emulator ")]              // DNS no distingue mayúsculas; trim del env
    public void Host_extra_con_opt_in(string host, string allowed) =>
        Assert.True(EmulatorAwsOptions.IsAllowedHost(host, allowed));
}
