using System.Security.Cryptography;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Alta idempotente del user pool y su app client, publicando sus ids en Parameter Store (D-0024):
/// Cognito genera los ids, así que los servicios no pueden conocerlos de antemano.
/// </summary>
public static class CognitoSetup
{
    public const string UserPoolName = "ticketflow-users";
    public const string ClientName = "ticketflow-cli";

    // shortcut: serializa solo dentro del proceso; dos procesos en frío a la vez podrían crear
    // dos pools con el mismo nombre. Correr provision antes de paralelizar.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<(string PoolId, string ClientId)> EnsureAsync(
        IAmazonCognitoIdentityProvider cognito, IAmazonSimpleSystemsManagement ssm, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var poolId = await FindPoolIdAsync(cognito, ct)
                ?? (await cognito.CreateUserPoolAsync(new CreateUserPoolRequest { PoolName = UserPoolName }, ct))
                    .UserPool.Id;
            var clientId = await FindClientIdAsync(cognito, poolId, ct)
                ?? (await cognito.CreateUserPoolClientAsync(new CreateUserPoolClientRequest
                {
                    UserPoolId = poolId,
                    ClientName = ClientName,
                    // Sin client secret: es un cliente "público" (CLI/SPA), no puede guardar secretos.
                    ExplicitAuthFlows = ["ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"],
                }, ct)).UserPoolClient.ClientId;
            await PutAsync(ssm, ParameterNames.UserPoolId, poolId, overwrite: true, ct);
            await PutAsync(ssm, ParameterNames.ClientId, clientId, overwrite: true, ct);
            return (poolId, clientId);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Escribe un parámetro; con overwrite=false respeta el valor que ya hubiera.</summary>
    public static async Task PutAsync(IAmazonSimpleSystemsManagement ssm, string name, string value,
        bool overwrite, CancellationToken ct = default)
    {
        try
        {
            await ssm.PutParameterAsync(new PutParameterRequest
            {
                Name = name, Value = value, Type = ParameterType.String, Overwrite = overwrite,
            }, ct);
        }
        catch (ParameterAlreadyExistsException) when (!overwrite)
        {
        }
    }

    public static async Task<string?> FindPoolIdAsync(IAmazonCognitoIdentityProvider cognito, CancellationToken ct)
    {
        string? next = null;
        do
        {
            var page = await cognito.ListUserPoolsAsync(
                new ListUserPoolsRequest { MaxResults = 60, NextToken = next }, ct);
            if (page.UserPools?.FirstOrDefault(p => p.Name == UserPoolName) is { } pool) return pool.Id;
            next = page.NextToken;
        } while (!string.IsNullOrEmpty(next));
        return null;
    }

    public static async Task<string?> FindClientIdAsync(
        IAmazonCognitoIdentityProvider cognito, string poolId, CancellationToken ct)
    {
        var page = await cognito.ListUserPoolClientsAsync(
            new ListUserPoolClientsRequest { UserPoolId = poolId, MaxResults = 60 }, ct);
        return page.UserPoolClients?.FirstOrDefault(c => c.ClientName == ClientName)?.ClientId;
    }

    /// <summary>
    /// Usuario de demo SOLO para el emulador: lo crea si falta, le pone una contraseña aleatoria
    /// nueva (no se guarda en ningún lado) e inicia sesión como lo haría un cliente real.
    /// </summary>
    public static async Task<AuthenticationResultType> SignInDemoUserAsync(IAmazonCognitoIdentityProvider cognito,
        IAmazonSimpleSystemsManagement ssm, string username, CancellationToken ct = default)
    {
        var (poolId, clientId) = await EnsureAsync(cognito, ssm, ct);
        try
        {
            await cognito.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = poolId, Username = username, MessageAction = MessageActionType.SUPPRESS,
            }, ct);
        }
        catch (UsernameExistsException)
        {
        }
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await cognito.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
        {
            UserPoolId = poolId, Username = username, Password = password, Permanent = true,
        }, ct);
        var response = await cognito.InitiateAuthAsync(new InitiateAuthRequest
        {
            AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
            ClientId = clientId,
            AuthParameters = new() { ["USERNAME"] = username, ["PASSWORD"] = password },
        }, ct);
        return response.AuthenticationResult;
    }
}
