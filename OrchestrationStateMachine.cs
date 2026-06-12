using System;
using System.Linq;

namespace test
{
    /// <summary>
    /// Orchestrator v1 的任務執行狀態機。
    /// 包住一份 OrchestrationPlanPayload，在執行過程中就地更新各 stage 與整體狀態，
    /// workspace 顯示端讀的是同一個 payload 參照，所以狀態變化會即時反映。
    /// 狀態流轉：pending → running → success / failed；pending → skipped。
    /// 整體狀態：pending → running → success / failed / partial。
    /// </summary>
    public sealed class OrchestrationStateMachine
    {
        public const string StatusPending = "pending";
        public const string StatusRunning = "running";
        public const string StatusSuccess = "success";
        public const string StatusFailed = "failed";
        public const string StatusPartial = "partial";
        public const string StatusSkipped = "skipped";

        private readonly OrchestrationPlanPayload _plan;
        private readonly object _sync = new();

        public OrchestrationStateMachine(OrchestrationPlanPayload plan)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));

            lock (_sync)
            {
                foreach (var stage in _plan.Stages ?? Array.Empty<OrchestrationStagePayload>())
                {
                    if (stage != null)
                        stage.Status = StatusPending;
                }

                _plan.Status = StatusRunning;
            }
        }

        public OrchestrationPlanPayload Plan => _plan;

        public void MarkRunning(string stageId)
            => Transition(stageId, StatusRunning, null, from: new[] { StatusPending });

        public void MarkSuccess(string stageId, string? detail = null)
            => Transition(stageId, StatusSuccess, detail, from: new[] { StatusPending, StatusRunning });

        public void MarkFailed(string stageId, string? detail = null)
            => Transition(stageId, StatusFailed, detail, from: new[] { StatusPending, StatusRunning });

        public void MarkSkipped(string stageId, string? reason = null)
            => Transition(stageId, StatusSkipped, reason, from: new[] { StatusPending });

        public void MarkCapabilityRunning(string capabilityId)
            => MarkRunning(CapabilityStageId(capabilityId));

        public void MarkCapabilitySuccess(string capabilityId, string? detail = null)
            => MarkSuccess(CapabilityStageId(capabilityId), detail);

        public void MarkCapabilityFailed(string capabilityId, string? detail = null)
            => MarkFailed(CapabilityStageId(capabilityId), detail);

        public void MarkCapabilitySkipped(string capabilityId, string? reason = null)
            => MarkSkipped(CapabilityStageId(capabilityId), reason);

        /// <summary>
        /// 整輪執行結束時呼叫：把還停在 pending/running 的 stage 收斂為 skipped，
        /// 並依最終執行結果與各 stage 狀態算出整體狀態。
        /// </summary>
        public string CompleteRun(bool executionSuccess, string? failureDetail = null)
        {
            lock (_sync)
            {
                foreach (var stage in _plan.Stages ?? Array.Empty<OrchestrationStagePayload>())
                {
                    if (stage == null)
                        continue;

                    if (stage.Status == StatusPending)
                        stage.Status = StatusSkipped;
                    else if (stage.Status == StatusRunning)
                        stage.Status = executionSuccess ? StatusSuccess : StatusFailed;
                }

                bool anyStageFailed = (_plan.Stages ?? Array.Empty<OrchestrationStagePayload>())
                    .Any(s => s != null && s.Status == StatusFailed);

                if (!executionSuccess)
                    _plan.Status = StatusFailed;
                else if (anyStageFailed)
                    _plan.Status = StatusPartial;
                else
                    _plan.Status = StatusSuccess;

                if (!executionSuccess && !string.IsNullOrWhiteSpace(failureDetail))
                {
                    var finalStage = FindStage("final_synthesis");
                    if (finalStage != null && string.IsNullOrWhiteSpace(finalStage.Detail))
                        finalStage.Detail = Truncate(failureDetail, 200);
                }

                return _plan.Status;
            }
        }

        /// <summary>執行中途因例外整輪中止時呼叫（required capability 失敗、缺新鮮事實等）。</summary>
        public void FailRun(string stageId, string reason)
        {
            MarkFailed(stageId, reason);
            CompleteRun(executionSuccess: false, failureDetail: reason);
        }

        public static string CapabilityStageId(string capabilityId)
            => $"capability:{capabilityId ?? ""}";

        private void Transition(string stageId, string toStatus, string? detail, string[] from)
        {
            if (string.IsNullOrWhiteSpace(stageId))
                return;

            lock (_sync)
            {
                var stage = FindStage(stageId);
                if (stage == null)
                    return;

                // 不允許從終態倒退（success/failed/skipped 之後不再變動）
                if (!from.Contains(stage.Status, StringComparer.Ordinal))
                    return;

                stage.Status = toStatus;

                if (!string.IsNullOrWhiteSpace(detail))
                    stage.Detail = Truncate(detail, 200);
            }
        }

        private OrchestrationStagePayload? FindStage(string stageId)
        {
            return (_plan.Stages ?? Array.Empty<OrchestrationStagePayload>())
                .FirstOrDefault(s => s != null &&
                    string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase));
        }

        private static string Truncate(string text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
