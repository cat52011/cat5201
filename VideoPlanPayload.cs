using System;
using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// Video Gen v1（多工具導演流程）：Claude 當「導演」產出的結構化影片計畫。
    /// 這是流程的核心、且不需任何影片 API 即可產出 —— 即使 Veo 3 等休眠，使用者仍拿到完整劇本/分鏡/旁白/鏡頭。
    /// 各專屬工具（Flux/Midjourney 關鍵畫面、Veo 3 影片、ElevenLabs 旁白、Suno 配樂）的分工與狀態記在 ProviderRoles。
    /// </summary>
    public sealed class VideoPlanPayload
    {
        public string Title { get; set; } = "";

        public string Logline { get; set; } = "";          // 一句話核心概念

        public string StyleDefinition { get; set; } = "";   // 風格定義（給 Flux/Midjourney + Veo）

        public string MusicBrief { get; set; } = "";        // 配樂方向（給 Suno）

        public int TotalDurationSeconds { get; set; }

        public IReadOnlyList<VideoScenePayload> Scenes { get; set; } = Array.Empty<VideoScenePayload>();

        // 工具分工表（劇本/關鍵畫面/影片/旁白/配樂 → 由誰負責、狀態）。
        public List<VideoProviderRole> ProviderRoles { get; set; } = new();

        // Claude 為 Veo 3 合成的最終 prompt（整合風格 + 各鏡頭）。
        public string VideoPromptForGenerator { get; set; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class VideoScenePayload
    {
        public int Index { get; set; }
        public string Narration { get; set; } = "";        // 旁白（給 ElevenLabs）
        public string Visual { get; set; } = "";           // 畫面描述
        public string Camera { get; set; } = "";           // 鏡頭設計（運鏡 / 構圖）
        public string KeyframePrompt { get; set; } = "";   // 關鍵畫面 prompt（給 Flux/Midjourney）
        public int DurationSeconds { get; set; }
    }

    public sealed class VideoProviderRole
    {
        public string Role { get; set; } = "";       // 劇本/分鏡/旁白/鏡頭、關鍵畫面/風格、影片、旁白配音、配樂
        public string Provider { get; set; } = "";   // Claude / Flux / Midjourney / Veo 3 / ElevenLabs / Suno
        public string Status { get; set; } = "";     // completed / skipped_no_api / failed / planned
        public string Detail { get; set; } = "";

        public static VideoProviderRole Of(string role, string provider, string status, string detail = "")
            => new VideoProviderRole { Role = role, Provider = provider, Status = status, Detail = detail };
    }

    public static class VideoProviderRoleStatus
    {
        public const string Completed = "completed";
        public const string SkippedNoApi = "skipped_no_api";
        public const string Failed = "failed";
        public const string Planned = "planned";

        public static string ToLabel(string status) => (status ?? "").Trim().ToLowerInvariant() switch
        {
            Completed => "完成",
            SkippedNoApi => "略過（無 API）",
            Failed => "失敗",
            Planned => "已規劃",
            _ => status ?? ""
        };
    }
}
