using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PanelFleet.Infrastructure.Security;

namespace PanelFleet.Client.UnitTests;

public sealed class NodeSecretProtectorTests
{
    [Fact]
    public void Protect_RoundTripsAndMasks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panelfleet-protector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(directory));
            var protector = new DataProtectionNodeSecretProtector(provider);

            var secret = protector.Protect("test-api-key-1234");

            Assert.NotEqual("test-api-key-1234", secret.CipherText);
            Assert.Equal("1234", secret.Hint);
            Assert.Equal("test-api-key-1234", protector.Unprotect(secret.CipherText));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Unprotect_InvalidCipher_Throws()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panelfleet-protector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(directory));
            var protector = new DataProtectionNodeSecretProtector(provider);
            var garbage = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            Assert.Throws<CryptographicException>(() => protector.Unprotect(garbage));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
