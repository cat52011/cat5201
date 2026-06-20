using System;

namespace test
{
    /// <summary>
    /// Video Gen v1 的「配角」工具供應點（旁白配音 / 配樂）。
    /// 依使用者指示：這些是配角，沒有 API 就跳過。目前為休眠可用性閘門——
    /// 流程依 IsConfigured 決定要不要跑；未設金鑰時記為「略過（無 API）」，不影響主流程。
    /// 取得 API 後，於各服務補上實際的 GenerateAsync 並在 AgentRuntime.RunSupportingMediaStages 接線即可。
    /// </summary>
    public sealed class ElevenLabsNarrationService
    {
        private readonly string _apiKey;

        public ElevenLabsNarrationService()
        {
            _apiKey = ApiKeyStore.Resolve("ELEVENLABS_API_KEY");
        }

        public string ProviderName => "ElevenLabs";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string NotConfiguredReason =>
            "旁白配音未啟用：未設定 ELEVENLABS_API_KEY（配角，可略過）。";
    }

    public sealed class SunoMusicService
    {
        private readonly string _apiKey;

        public SunoMusicService()
        {
            _apiKey = ApiKeyStore.Resolve("SUNO_API_KEY");
        }

        public string ProviderName => "Suno";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string NotConfiguredReason =>
            "配樂未啟用：未設定 SUNO_API_KEY（配角，可略過）。";
    }
}
