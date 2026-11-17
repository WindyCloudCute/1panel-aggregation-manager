using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.Signing;

/// <summary>1Panel API Key 签名计算。</summary>
internal static class ApiKeySigner
{
    public static string ComputeToken(string apiKey, long unixTimestamp, AuthMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        var timestamp = unixTimestamp.ToString(CultureInfo.InvariantCulture);
        return mode switch
        {
            AuthMode.HmacSha256 => ComputeHmac(apiKey, timestamp),
            AuthMode.Md5 => ComputeMd5(apiKey, timestamp),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Auto 模式必须先解析为具体鉴权方式")
        };
    }

    private static string ComputeHmac(string apiKey, string timestamp)
    {
        var key = Encoding.UTF8.GetBytes(apiKey);
        var message = Encoding.UTF8.GetBytes("1panel:" + timestamp);
        var mac = HMACSHA256.HashData(key, message);
        return Convert.ToHexStringLower(mac);
    }

    private static string ComputeMd5(string apiKey, string timestamp)
    {
        var bytes = Encoding.UTF8.GetBytes("1panel" + apiKey + timestamp);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
