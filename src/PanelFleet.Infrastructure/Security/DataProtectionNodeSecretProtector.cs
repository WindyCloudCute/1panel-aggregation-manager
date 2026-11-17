using System.Text;
using Microsoft.AspNetCore.DataProtection;
using PanelFleet.Application.Abstractions;

namespace PanelFleet.Infrastructure.Security;

/// <summary>基于 ASP.NET DataProtection 的 API Key 加密（DPAPI/密钥环由 Web 层配置）。</summary>
public sealed class DataProtectionNodeSecretProtector : INodeSecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionNodeSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("PanelFleet.NodeApiKeys.v1");
    }

    public ProtectedSecret Protect(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var cipherText = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(apiKey)));
        var hint = apiKey.Length >= 4 ? apiKey[^4..] : "••••";
        return new ProtectedSecret(cipherText, hint);
    }

    public string Unprotect(string cipherText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherText);

        var bytes = _protector.Unprotect(Convert.FromBase64String(cipherText));
        return Encoding.UTF8.GetString(bytes);
    }
}
