using System.Security.Cryptography;
using System.Text;

namespace MarketIntelligence.Agent.Api;

public static class ServiceAuthorization
{
    public static bool IsAuthorized(
        HttpRequest request,
        IConfiguration configuration,
        string configurationKey)
    {
        var expectedKey = configuration[configurationKey];
        var suppliedKey = request.Headers["X-Agent-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expectedKey) || string.IsNullOrWhiteSpace(suppliedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedKey),
            Encoding.UTF8.GetBytes(suppliedKey));
    }
}
