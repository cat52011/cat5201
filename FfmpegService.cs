using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// Video Gen v1.5：本地 ffmpeg 影片剪接（給「快剪」模式用）。
    ///
    /// 「快剪 / 預告片」節奏無法靠 Veo 原生延伸做出來（延伸＝同一連續鏡頭 +7 秒，永遠是長鏡頭）。
    /// 做法改成：獨立生成多個短鏡頭（各 4 秒）→ 用 ffmpeg 各裁前 N 秒 → 拼接成一支快剪影片。
    ///
    /// 休眠 / 優雅降級：系統沒有 ffmpeg 時 IsAvailable=false，呼叫端會自動退回連貫（延伸）模式，
    /// 不影響主流程。偵測順序：環境變數 CAT5201_FFMPEG → PATH 的 ffmpeg → 常見安裝路徑。
    /// 裝了 ffmpeg（winget install Gyan.FFmpeg / choco install ffmpeg）後快剪自動生效。
    /// </summary>
    public sealed class FfmpegService
    {
        private readonly string _ffmpegPath;

        public FfmpegService()
        {
            _ffmpegPath = ResolveFfmpegPath();
        }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath);

        public string FfmpegPath => _ffmpegPath;

        public string NotAvailableReason =>
            "找不到 ffmpeg（快剪模式需要它做本地裁切拼接）。安裝後自動啟用：winget install Gyan.FFmpeg，或設環境變數 CAT5201_FFMPEG 指向 ffmpeg.exe。";

        private static string ResolveFfmpegPath()
        {
            // 1) 明確指定
            string env = Environment.GetEnvironmentVariable("CAT5201_FFMPEG") ?? "";
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            // 2) PATH 中的 ffmpeg（用 where 探測，找不到回空）
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = "ffmpeg",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                string first = outp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first))
                    return first;
            }
            catch { /* 偵測失敗視為沒有 */ }

            // 3) 常見安裝路徑
            foreach (var cand in CommonInstallPaths())
            {
                if (File.Exists(cand))
                    return cand;
            }

            // 4) winget 可攜式套件（Gyan.FFmpeg）：真正的 exe 在 WinGet\Packages\*ffmpeg*\...\bin\ffmpeg.exe，
            //    且不一定有 Links shim、PATH 要重開 shell 才生效——直接到 Packages 目錄找最保險。
            string wingetExe = FindInWingetPackages();
            if (!string.IsNullOrWhiteSpace(wingetExe))
                return wingetExe;

            return "";
        }

        private static IEnumerable<string> CommonInstallPaths()
        {
            string programData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return @"C:\ffmpeg\bin\ffmpeg.exe";
            yield return Path.Combine(programData, "chocolatey", "bin", "ffmpeg.exe");
            // winget link shims
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        }

        // 在 %LocalAppData%\Microsoft\WinGet\Packages 下，找名稱含 ffmpeg 的套件目錄裡的 ffmpeg.exe。
        // 範圍限縮在 *ffmpeg* 子目錄，避免掃整個 Packages 太慢。
        private static string FindInWingetPackages()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (!Directory.Exists(packages))
                    return "";

                foreach (var dir in Directory.GetDirectories(packages, "*ffmpeg*")) // Windows 檔名比對不分大小寫
                {
                    var found = Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            catch { /* 偵測失敗視為沒有 */ }

            return "";
        }

        /// <summary>
        /// 把多個短片段各裁前 keepSeconds 秒後依序拼接成一支影片，回傳成品 mp4 bytes。
        /// 視訊統一重編碼（H.264 / yuv420p / 24fps / 720p 寬高比保持），確保不同段能無縫 concat。
        /// 為避免硬切爆音，快剪只保留視訊（音訊丟棄，配樂留後製）。
        /// 任一步失敗回傳 (false, error, empty)。
        /// </summary>
        public async Task<(bool Success, string Error, byte[] Bytes)> TrimAndConcatAsync(
            IReadOnlyList<(byte[] Mp4, double KeepSeconds)> clips,
            CancellationToken ct = default)
        {
            if (!IsAvailable)
                return (false, NotAvailableReason, Array.Empty<byte>());

            var usable = (clips ?? new List<(byte[], double)>())
                .Where(c => c.Mp4 != null && c.Mp4.Length > 0 && c.KeepSeconds > 0)
                .ToList();

            if (usable.Count == 0)
                return (false, "沒有可拼接的片段。", Array.Empty<byte>());

            string workDir = Path.Combine(Path.GetTempPath(), "cat5201_cut_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                var inputArgs = new StringBuilder();
                var filter = new StringBuilder();
                var concatRefs = new StringBuilder();

                for (int i = 0; i < usable.Count; i++)
                {
                    string inPath = Path.Combine(workDir, $"in_{i}.mp4");
                    await File.WriteAllBytesAsync(inPath, usable[i].Mp4, ct);

                    inputArgs.Append($"-i \"{inPath}\" ");

                    // 每段：裁前 KeepSeconds 秒 → 重設時間戳 → 統一 fps/尺寸（保持寬高比，補黑邊到 1280x720）。
                    double keep = Math.Max(0.5, usable[i].KeepSeconds);
                    string ks = keep.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    filter.Append(
                        $"[{i}:v]trim=0:{ks},setpts=PTS-STARTPTS,fps=24," +
                        "scale=1280:720:force_original_aspect_ratio=decrease," +
                        $"pad=1280:720:(ow-iw)/2:(oh-ih)/2[v{i}];");
                    concatRefs.Append($"[v{i}]");
                }

                filter.Append($"{concatRefs}concat=n={usable.Count}:v=1:a=0[outv]");

                string outPath = Path.Combine(workDir, "out.mp4");
                string args =
                    $"-y {inputArgs}-filter_complex \"{filter}\" -map \"[outv]\" " +
                    $"-c:v libx264 -pix_fmt yuv420p -preset medium -movflags +faststart \"{outPath}\"";

                var (ok, stderr) = await RunFfmpegAsync(args, ct);
                if (!ok || !File.Exists(outPath))
                    return (false, $"ffmpeg 拼接失敗：{Truncate(stderr, 400)}", Array.Empty<byte>());

                byte[] bytes = await File.ReadAllBytesAsync(outPath, ct);
                return bytes.Length > 0
                    ? (true, "", bytes)
                    : (false, "ffmpeg 產出空檔。", Array.Empty<byte>());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, $"剪接發生例外：{ex.Message}", Array.Empty<byte>());
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* 暫存清理失敗忽略 */ }
            }
        }

        private async Task<(bool Ok, string Stderr)> RunFfmpegAsync(string args, CancellationToken ct)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stderr = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            p.Start();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                throw;
            }

            return (p.ExitCode == 0, stderr.ToString());
        }

        private static string Truncate(string s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
