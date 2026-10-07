using System.Text.Json.Serialization;

namespace ClassDailyLand.Core.Models;

/// <summary>
/// 用户协议同意状态（settings/agreement.json）。
/// 对应源模块：agreement.py 的 load_agreement_status / save_agreement_status。
/// </summary>
public sealed class AgreementState
{
    [JsonPropertyName("agreed")]
    public bool Agreed { get; set; }
}
