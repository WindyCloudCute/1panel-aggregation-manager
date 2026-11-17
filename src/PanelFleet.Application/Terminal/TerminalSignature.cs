using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 1Panel API Key 签名（与 <c>PanelFleet.Client.Signing.ApiKeySigner</c> 规则一致）。
/// 1Panel 的终端 WebSocket 复用同一套 REST 签名头：
/// <c>1Panel-Token</c> + <c>1Panel-Timestamp</c>。
/// </summary>
public static class TerminalSignature
{
    /// <summary>计算签名：HMAC-SHA256(key=apiKey, msg="1panel:"+timestamp) 或 MD5("1panel"+apiKey+timestamp)，均为小写 hex。</summary>
    public static string Compute(string apiKey, long unixTimestamp, AuthMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        var timestamp = unixTimestamp.ToString(CultureInfo.InvariantCulture);
        return mode switch
        {
            AuthMode.HmacSha256 => ComputeHmac(apiKey, timestamp),
            AuthMode.Md5 => ComputeMd5(apiKey, timestamp),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Auto 模式必须先解析为具体鉴权方式。")
        };
    }

    private static string ComputeHmac(string apiKey, string timestamp)
    {
        var key = Encoding.UTF8.GetBytes(apiKey);
        var message = Encoding.UTF8.GetBytes("1panel:" + timestamp);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, message));
    }

    private static string ComputeMd5(string apiKey, string timestamp)
    {
        var bytes = Encoding.UTF8.GetBytes("1panel" + apiKey + timestamp);
        return Convert.ToHexStringLower(MD5.HashData(bytes));
    }
}
