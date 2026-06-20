using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace test
{
    /// <summary>
    /// 集中式 API 金鑰來源（單一真相）。所有 AI 服務一律透過這裡取金鑰，不再各自直接讀環境變數。
    ///
    /// 解析順序（對應使用者需求）：
    ///   1. 系統環境變數（最優先；給進階使用者 / CI / 不想把金鑰寫進 app 的人）
    ///   2. 使用者在 app 內「API 設定頁」輸入並儲存的金鑰（接口）
    ///   3. 都沒有 → 回空字串，服務建構子會丟例外 → 由 fallback 鏈自動切到其他可用模型，
    ///      全部不可用才輸出失敗與原因。（防禦機制在 NodeService.ExecuteWithFallbackAsync）
    ///
    /// 儲存安全：使用者輸入的金鑰以 Windows DPAPI（CurrentUser 範圍）加密後落地，
    /// 只有同一 Windows 使用者帳號能解密，避免明文金鑰躺在磁碟上。
    /// </summary>
    public static class ApiKeyStore
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string> _userKeys = new(StringComparer.OrdinalIgnoreCase);
        private static string? _filePath;

        /// <summary>app 實際會用到的金鑰名稱（顯示在 API 設定頁的欄位）。</summary>
        public static readonly (string EnvName, string Label, string Hint)[] KnownKeys =
        {
            ("OPENAI_API_KEY",     "OpenAI",     "GPT 文字、gpt-image 圖片"),
            ("ANTHROPIC_API_KEY",  "Claude",     "Anthropic Claude 文字 / 導演 / 程式碼"),
            ("GEMINI_API_KEY",     "Gemini",     "Google Gemini 文字、Veo 影片（Google 生態）"),
            ("PERPLEXITY_API_KEY", "Perplexity", "即時聯網研究 / 來源查證"),
        };

        public enum KeySource { None, Environment, UserEntered }

        /// <summary>啟動時呼叫一次：指定設定資料夾並載入已儲存的金鑰。必須在任何 AI 服務建構之前。</summary>
        public static void Initialize(string configDir)
        {
            lock (_lock)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(configDir))
                    {
                        Directory.CreateDirectory(configDir);
                        _filePath = Path.Combine(configDir, "_apikeys.json");
                    }
                    LoadLocked();
                }
                catch
                {
                    // 載入失敗不擋啟動；當作沒有使用者金鑰，仍可用環境變數。
                }
            }
        }

        /// <summary>解析單一金鑰：環境變數優先，其次使用者輸入；都沒有回空字串。</summary>
        public static string Resolve(string envName)
        {
            if (string.IsNullOrWhiteSpace(envName))
                return "";

            string env = Environment.GetEnvironmentVariable(envName) ?? "";
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            lock (_lock)
            {
                if (_userKeys.TryGetValue(envName, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return "";
        }

        /// <summary>多個候選環境變數名（例：GEMINI_API_KEY / GOOGLE_API_KEY），回第一個有值的。</summary>
        public static string ResolveAny(params string[] envNames)
        {
            if (envNames == null)
                return "";
            foreach (var n in envNames)
            {
                var v = Resolve(n);
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return "";
        }

        /// <summary>是否有任一來源能提供此金鑰（給 UI 顯示「可用」用）。</summary>
        public static bool HasKey(string envName) => !string.IsNullOrWhiteSpace(Resolve(envName));

        /// <summary>此金鑰目前的來源（環境變數 / 使用者輸入 / 無）。給 API 設定頁顯示狀態用。</summary>
        public static KeySource GetSource(string envName)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName)))
                return KeySource.Environment;
            lock (_lock)
            {
                if (_userKeys.TryGetValue(envName, out var v) && !string.IsNullOrWhiteSpace(v))
                    return KeySource.UserEntered;
            }
            return KeySource.None;
        }

        /// <summary>取回使用者輸入的金鑰原文（給設定頁回填；環境變數不在此回傳）。</summary>
        public static string GetUserKey(string envName)
        {
            lock (_lock)
            {
                return _userKeys.TryGetValue(envName, out var v) ? (v ?? "") : "";
            }
        }

        /// <summary>一次寫入多組使用者金鑰（空值代表清除該欄），加密落地。</summary>
        public static void SetUserKeys(IDictionary<string, string?> values)
        {
            if (values == null)
                return;

            lock (_lock)
            {
                foreach (var kv in values)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;
                    if (string.IsNullOrWhiteSpace(kv.Value))
                        _userKeys.Remove(kv.Key);
                    else
                        _userKeys[kv.Key] = kv.Value.Trim();
                }
                SaveLocked();
            }
        }

        // ===== 內部：加密落地 / 解密載入 =====

        private static void LoadLocked()
        {
            _userKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
                return;

            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));
            if (raw == null)
                return;

            foreach (var kv in raw)
            {
                string plain = TryUnprotect(kv.Value);
                if (!string.IsNullOrWhiteSpace(plain))
                    _userKeys[kv.Key] = plain;
            }
        }

        private static void SaveLocked()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
                return;

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var encrypted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _userKeys)
            {
                if (string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                encrypted[kv.Key] = Protect(kv.Value);
            }

            var json = JsonSerializer.Serialize(encrypted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }

        // DPAPI（CurrentUser）：只有同一 Windows 帳號能解密。失敗時退回 Base64（至少不是肉眼明文）。
        private static string Protect(string plain)
        {
            try
            {
                byte[] enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(enc);
            }
            catch
            {
                return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
            }
        }

        private static string TryUnprotect(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "";
            try
            {
                if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
                {
                    byte[] enc = Convert.FromBase64String(stored.Substring("dpapi:".Length));
                    byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dec);
                }
                if (stored.StartsWith("b64:", StringComparison.Ordinal))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring("b64:".Length)));

                // 無前綴：視為舊格式明文，直接採用（向後相容）。
                return stored;
            }
            catch
            {
                // 解密失敗（例如金鑰檔從別台機器複製過來）→ 當作未設定。
                return "";
            }
        }
    }
}
