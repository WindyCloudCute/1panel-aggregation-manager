namespace PanelFleet.Application.Abstractions;

/// <summary>API Key 加密结果。</summary>
public sealed record ProtectedSecret(string CipherText, string Hint);

public interface INodeSecretProtector
{
    ProtectedSecret Protect(string apiKey);

    string Unprotect(string cipherText);
}
