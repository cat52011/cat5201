namespace test
{
    /// <summary>
    /// §4 自動下游節點：多階段任務被偵測後的觸發策略（個人化設定可切換）。
    /// </summary>
    public enum DownstreamAutoMode
    {
        /// <summary>一鍵展開並自動執行（預設）：偵測到多階段任務時提供一鍵入口，點了才花 token。</summary>
        OneClick = 0,

        /// <summary>完全自動：使用者送出後，若偵測為多階段任務，立即自動展開下游節點並依序執行。</summary>
        FullyAuto = 1,

        /// <summary>關閉：不自動展開，只用單一節點作答（仍可由右鍵選單手動展開）。</summary>
        Off = 2
    }

    public static class DownstreamAutoModeHelper
    {
        public static DownstreamAutoMode Parse(string? value)
        {
            return (value ?? "").Trim().ToLowerInvariant() switch
            {
                "fullyauto" or "fully_auto" or "auto" or "2" => DownstreamAutoMode.FullyAuto,
                "off" or "disabled" or "none" or "3" => DownstreamAutoMode.Off,
                _ => DownstreamAutoMode.OneClick
            };
        }

        public static string ToStorageValue(DownstreamAutoMode mode)
        {
            return mode switch
            {
                DownstreamAutoMode.FullyAuto => "FullyAuto",
                DownstreamAutoMode.Off => "Off",
                _ => "OneClick"
            };
        }
    }
}
