using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PanelFleet.Application.Configuration;

/// <summary>持久化的 PanelFleet 设置文档（JSON 文件）。</summary>
public sealed record PanelFleetSettingsDocument(
    long Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>PanelFleet 设置持久化抽象（默认实现为本地 JSON 文件；可替换为 EF 表实现）。</summary>
public interface IPanelFleetSettingsStore
{
    /// <summary>读取持久化文档；文件不存在或损坏时返回 <c>null</c>（损坏时记录日志并忽略）。</summary>
    PanelFleetSettingsDocument? Load();

    /// <summary>写入持久化文档（覆盖写，调用方保证串行）。</summary>
    void Save(PanelFleetSettingsDocument document);
}

/// <summary>
/// 基于单文件 JSON 的 PanelFleet 设置存储。
/// <para>写入先落同目录临时文件再原子替换，避免进程中断留下半截文件；读取失败视为「无持久化设置」。</para>
/// </summary>
public sealed class JsonFilePanelFleetSettingsStore : IPanelFleetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly ILogger<JsonFilePanelFleetSettingsStore> _logger;

    public JsonFilePanelFleetSettingsStore(string filePath, ILogger<JsonFilePanelFleetSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
    }

    /// <summary>设置文件绝对路径（供启动日志与设置页展示）。</summary>
    public string FilePath => _filePath;

    public PanelFleetSettingsDocument? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<PanelFleetSettingsDocument>(json, SerializerOptions);
            if (document is null)
            {
                return null;
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document.Values)
            {
                values[pair.Key] = pair.Value;
            }

            _logger.LogInformation("已加载 PanelFleet 设置文件 {FilePath}（revision {Revision}）", _filePath, document.Revision);
            return document with { Values = values };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取 PanelFleet 设置文件失败，将回退到默认/配置值：{FilePath}", _filePath);
            return null;
        }
    }

    public void Save(PanelFleetSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
