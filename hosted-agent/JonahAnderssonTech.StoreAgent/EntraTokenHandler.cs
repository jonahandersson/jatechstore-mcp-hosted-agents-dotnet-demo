using System.Net.Http.Headers;
using Azure.Core;

namespace JonahAnderssonTech.StoreAgent;

/// <summary>
/// Adds a Microsoft Entra bearer token to every MCP request and refreshes it before it expires.
/// In Foundry the token belongs to the hosted agent's own agent identity, so no secret is stored anywhere.
/// </summary>
internal sealed class EntraTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _token;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        // Refresh five minutes before expiry.
        if (_token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return _token.Token;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                _token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            }
            return _token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
