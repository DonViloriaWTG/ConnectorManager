using Newtonsoft.Json;

namespace ConnectorManager.Models;

/// <summary>
/// Maps to the ConnectorDeploymentInfo.json found in each carrier connector folder.
/// </summary>
public sealed class ConnectorDeploymentInfo
{
    [JsonProperty("connectorName")]
    public string ConnectorName { get; set; } = string.Empty;

    [JsonProperty("relativeProjectPath")]
    public string RelativeProjectPath { get; set; } = string.Empty;

    [JsonProperty("majorVersion")]
    public int MajorVersion { get; set; }

    [JsonProperty("minorVersion")]
    public int MinorVersion { get; set; }

    [JsonProperty("carrierCode")]
    public string CarrierCode { get; set; } = string.Empty;

    [JsonProperty("carrierName")]
    public string CarrierName { get; set; } = string.Empty;

    [JsonProperty("countryCode")]
    public string CountryCode { get; set; } = string.Empty;
}
