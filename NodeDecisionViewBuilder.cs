using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class NodeDecisionViewBuilder
    {
        public static NodeDecisionViewData BuildFromLog(AiExecutionLogEntry log)
        {
            if (log == null)
                return CreateEmpty();

            string requestedLabel = GetModelLabel(log.RequestedModelId);
            string plannedLabel = GetModelLabel(log.PlannedModelId);
            string actualLabel = GetModelLabel(log.ActualModelId);

            string capabilityRequestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string capabilityResolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            string displayFromLabel = requestedLabel;
            string displayToLabel = actualLabel;

            string agent = string.IsNullOrWhiteSpace(log.ActualAgentId)
                ? (string.IsNullOrWhiteSpace(log.RequestedAgentId) ? "-" : log.RequestedAgentId)
                : log.ActualAgentId;

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                displayFromLabel = capabilityRequestedLabel;
                displayToLabel = capabilityResolvedLabel;
            }

            string modelLabel =
                string.Equals(displayFromLabel, displayToLabel, StringComparison.OrdinalIgnoreCase)
                    ? displayToLabel
                    : $"{displayToLabel} ← {displayFromLabel}";

            string status = log.SelectionMode switch
            {
                "API Auto" => "API Auto",
                "Auto" => "Rule Auto",
                _ => "Manual"
            };

            string mode = log.SelectionMode switch
            {
                "Manual" => "Manual",
                _ => "Auto"
            };

            string resolver = string.IsNullOrWhiteSpace(log.Resolver)
                ? "-"
                : log.Resolver;

            string taskSummary = BuildTaskSummary(log);

            string reason = string.IsNullOrWhiteSpace(log.ResolverReason)
                ? "-"
                : log.ResolverReason;

            string keywords = BuildKeywordSummary(log);
            if (string.IsNullOrWhiteSpace(keywords))
                keywords = "-";

            string capabilitySummary = BuildCapabilityTraceSummary(log);
            if (string.IsNullOrWhiteSpace(capabilitySummary))
                capabilitySummary = "-";

            var capabilityDetails = BuildCapabilityTraceDetails(log);

            string delegationSummary = "-";
            var delegationDetails = new List<string>();
            // 歷史 log 目前若尚未存 delegation trace，可先留空
            // 若你之後把 delegation trace 也加進 log，可在這裡一併接回

            string extra = BuildExtraSummary(log);
            if (string.IsNullOrWhiteSpace(extra))
                extra = "-";

            bool apiFallbackUsed =
                log.Resolver?.Contains("fallback", StringComparison.OrdinalIgnoreCase) == true;

            return new NodeDecisionViewData
            {
                Status = status,
                Mode = mode,
                Resolver = resolver,
                Agent = agent,
                Model = modelLabel,
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = extra,

                CapabilitySummary = capabilitySummary,
                CapabilityDetails = capabilityDetails,

                DelegationSummary = delegationSummary,
                DelegationDetails = delegationDetails,

                CapabilityAdjusted = log.CapabilityAdjusted,
                RuntimeFallbackUsed = log.RuntimeFallbackUsed,
                ApiFallbackUsed = apiFallbackUsed,

                Steps = BuildSteps(
                    log,
                    requestedLabel,
                    plannedLabel,
                    actualLabel,
                    resolver,
                    apiFallbackUsed)
            };
        }

        private static NodeDecisionViewData CreateEmpty()
        {
            return new NodeDecisionViewData
            {
                Status = "Manual",
                Mode = "Manual",
                Resolver = "-",
                Model = "-",
                TaskSummary = "-",
                Reason = "-",
                Keywords = "-",
                Extra = "-",
                CapabilitySummary = "-",
                CapabilityDetails = Array.Empty<string>(),
                DelegationSummary = "-",
                DelegationDetails = Array.Empty<string>(),
                Agent = "-",
                Steps = Array.Empty<NodeDecisionStepViewData>()
            };
        }

        private static IReadOnlyList<NodeDecisionStepViewData> BuildSteps(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel,
            string resolver,
            bool apiFallbackUsed)
        {
            var steps = new List<NodeDecisionStepViewData>();

            steps.Add(BuildTaskModeStep(log));
            steps.Add(BuildModelSelectionStep(log, requestedLabel, plannedLabel, actualLabel));
            steps.Add(BuildAiTeamStep(log));
            steps.Add(BuildResolverStep(log, resolver, apiFallbackUsed));
            steps.Add(BuildCapabilityStep(log));
            steps.Add(BuildMemoryStep(log));
            steps.Add(BuildWorkspaceStep(log));
            steps.Add(BuildFallbackStep(log, apiFallbackUsed));
            steps.Add(BuildExecutionStep(log));

            return steps;
        }

        private static NodeDecisionStepViewData BuildTaskModeStep(AiExecutionLogEntry log)
        {
            var lines = new List<string>
            {
                $"Task Mode: {Safe(log.TaskMode)}",
                $"Confidence: {log.Confidence:0.00}"
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add($"Reason: {log.ResolverReason}");

            if (log.ResolverKeywords != null && log.ResolverKeywords.Count > 0)
            {
                var keywords = log.ResolverKeywords
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                lines.Add("Keywords: " + string.Join(", ", keywords));
            }

            return new NodeDecisionStepViewData
            {
                Title = "Task Mode",
                Detail = $"{Safe(log.TaskMode)} / confidence {log.Confidence:0.00}",
                State = NodeDecisionStepState.Info,
                Highlight = true,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildModelSelectionStep(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel)
        {
            string summary;

            if (string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase))
                summary = actualLabel;
            else
                summary = $"{requestedLabel} → {actualLabel}";

            var lines = new List<string>
            {
                $"Requested Model: {requestedLabel}",
                $"Planned Model: {plannedLabel}",
                $"Actual Model: {actualLabel}"
            };

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                lines.Add($"Capability Redirect: {GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}");
            }

            // Multi-Model v1：顯示實際模型的能力標籤與成本層級。
            var actualDef = AiModelHelper.GetDefinition(log.ActualModelId);
            if (actualDef != null)
            {
                lines.Add($"成本層級：{actualDef.CostTierLabel}");
                lines.Add($"能力：{actualDef.CapabilitySummary}");
            }

            var state =
                string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(plannedLabel, actualLabel, StringComparison.OrdinalIgnoreCase)
                    ? NodeDecisionStepState.Success
                    : NodeDecisionStepState.Warning;

            return new NodeDecisionStepViewData
            {
                Title = "Model Selection",
                Detail = summary,
                State = state,
                Highlight = true,
                DetailLines = lines
            };
        }

        // 「參與的 AI」：用白話列出這個節點裡有哪些 AI 參與、各自做了什麼。
        //
        // 設計原則：以「產出物的種類（ArtifactKind）」反推是哪個 AI 做的，而不是讀每個 artifact
        // 自帶的 ModelId / SourceAgentId——後者對 facts/search/presentation/file 常常沒填或被覆寫，
        // 直接用會得到「圖片 — 事實」這種錯誤掛名。種類是可靠的，且每種產出物固定由哪個 AI 產生是已知的。
        // 同一個 AI 做的多件事聚合成一行（例：研究 AI — 查資料、查證事實）。
        private static NodeDecisionStepViewData BuildAiTeamStep(AiExecutionLogEntry log)
        {
            var roles = new List<TeamRole>();
            var index = new Dictionary<string, TeamRole>(StringComparer.Ordinal);

            TeamRole RoleFor(string key, string icon, string label, int order)
            {
                if (!index.TryGetValue(key, out var r))
                {
                    r = new TeamRole { Icon = icon, Label = label, Order = order };
                    index[key] = r;
                    roles.Add(r);
                }
                return r;
            }

            var artifacts = (log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(x => x != null)
                .ToList();

            foreach (var a in artifacts)
            {
                string kind = (a.ArtifactKind ?? "").Trim().ToLowerInvariant();
                string itemType = (a.ItemType ?? "").Trim().ToLowerInvariant();
                string format = (a.ContentFormat ?? "").Trim().ToLowerInvariant();
                string k = (kind.Length == 0 || kind == "artifact") ? itemType : kind;

                switch (k)
                {
                    // 編排 / workflow 計畫是「路由 meta」，不是某個 AI 做的內容交付，且其 model 是節點被路由到的
                    // 模型（簡報任務常被路由成 pplx），掛上去會誤導。這張卡只列真正做事的 AI，編排細節留在 Workspace/Capability 步驟。
                    case "workflow":
                    case "orchestration":
                    case "orchestration_plan":
                        break;

                    case "facts":
                    case "verified_facts":
                        RoleFor("research", "🔍", "研究 AI", 1).Add("查證事實", a.ModelId);
                        break;

                    case "search":
                    case "search_summary":
                        RoleFor("research", "🔍", "研究 AI", 1).Add("查資料", a.ModelId);
                        break;

                    case "analysis":
                    case "reasoning_analysis":
                        RoleFor("reasoning", "🧠", "分析 AI", 2).Add("分析推理", a.ModelId);
                        break;

                    case "presentation":
                    case "presentation_outline":
                        RoleFor("author", "📊", "簡報 AI", 3).Add("撰寫簡報", a.ModelId);
                        break;

                    case "media":
                    case "image":
                        if (format.Contains("video") || itemType.Contains("video"))
                            RoleFor("video", "🎬", "影片 AI", 5).Add("生成影片", a.ModelId);
                        else
                            RoleFor("image", "🖼", "圖片 AI", 4).Add("生成圖片", a.ModelId);
                        break;

                    case "video":
                        RoleFor("video", "🎬", "影片 AI", 5).Add("生成影片", a.ModelId);
                        break;

                    case "code":
                    case "code_snapshot":
                        RoleFor("coder", "💻", "程式 AI", 6).Add("產生程式碼", a.ModelId);
                        break;

                    case "diff":
                    case "patch":
                    case "code_diff":
                        RoleFor("coder", "💻", "程式 AI", 6).Add("產生修改", a.ModelId);
                        break;

                    case "final":
                    case "final_synthesis":
                        RoleFor("primary", "🧠", "統整 AI", 7).Add("統整最終答案", a.ModelId);
                        break;

                    case "file":
                    case "generated_file":
                        var f = RoleFor("file", "📄", "檔案輸出", 8);
                        f.IsSystem = true;
                        f.Add(StripGeneratedFilePrefix(a.Title), null);
                        break;

                    // sandbox / downstream_node_plan / 其餘 meta：不算「某個 AI 做了一件交付的事」，略過。
                }
            }

            // 純對話（沒有任何管線產出物）：就是主模型直接回答。
            bool hasAiRole = roles.Any(r => !r.IsSystem);
            if (!hasAiRole)
            {
                RoleFor("primary", "🧠", "", 7)
                    .Add("回答", log.ActualModelId);
            }

            var lines = roles
                .OrderBy(r => r.Order)
                .Select(r => r.Render())
                .ToList();

            int aiCount = roles.Count(r => !r.IsSystem);
            string detail = aiCount <= 1
                ? (roles.FirstOrDefault(r => !r.IsSystem)?.Head() ?? GetModelLabel(log.ActualModelId))
                : $"{aiCount} 個 AI 協作";

            return new NodeDecisionStepViewData
            {
                Title = "參與的 AI",
                Detail = detail,
                State = NodeDecisionStepState.Info,
                Highlight = aiCount > 1,
                DetailLines = lines
            };
        }

        // 一個參與角色（依產出物種類聚合）：固定的 AI 角色 + 它做過的事 + 已知的模型名。
        private sealed class TeamRole
        {
            public string Icon = "";
            public string Label = "";
            public string Model = "";
            public int Order;
            public bool IsSystem;
            private readonly List<string> _actions = new();

            public void Add(string action, string? modelId)
            {
                if (!string.IsNullOrWhiteSpace(action) && !_actions.Contains(action))
                    _actions.Add(action);

                // 只在 artifact 真的帶了模型 id 時才掛名，避免假資訊。
                if (string.IsNullOrWhiteSpace(Model) &&
                    !string.IsNullOrWhiteSpace(modelId) &&
                    modelId.Trim() != "-")
                {
                    Model = GetModelLabel(modelId);
                }
            }

            // 「角色（模型）」；模型未知就只顯示角色，不硬湊。
            public string Head()
            {
                if (string.IsNullOrWhiteSpace(Label))
                    return string.IsNullOrWhiteSpace(Model) ? "AI" : Model;
                return string.IsNullOrWhiteSpace(Model) ? Label : $"{Label}（{Model}）";
            }

            public string Render()
            {
                string actions = _actions.Count > 0 ? string.Join("、", _actions) : "—";
                return $"{Icon} {Head()} — {actions}";
            }
        }

        // "Generated File - NVIDIA….pptx" → "NVIDIA….pptx"；失敗則白話化。
        private static string StripGeneratedFilePrefix(string? title)
        {
            string t = (title ?? "").Trim();
            const string prefix = "Generated File - ";
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(prefix.Length).Trim();

            if (t.Length == 0 || string.Equals(t, "failed", StringComparison.OrdinalIgnoreCase))
                return "產生檔案失敗";

            return Trim(t, 40);
        }

        private static NodeDecisionStepViewData BuildResolverStep(
            AiExecutionLogEntry log,
            string resolver,
            bool apiFallbackUsed)
        {
            var lines = new List<string>
            {
                $"Resolver: {Safe(resolver)}",
                $"Selection Mode: {Safe(log.SelectionMode)}"
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add($"Resolver Reason: {log.ResolverReason}");

            if (apiFallbackUsed)
                lines.Add("API resolver failed or downgraded, fallback to rules.");

            return new NodeDecisionStepViewData
            {
                Title = "Resolver",
                Detail = Safe(resolver),
                State = apiFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildCapabilityStep(AiExecutionLogEntry log)
        {
            string detail;
            IReadOnlyList<string> lines;

            var traceSummary = BuildCapabilityTraceSummary(log);
            var traceDetails = BuildCapabilityTraceDetails(log);

            if (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-")
            {
                detail = traceSummary;
                lines = traceDetails.Count > 0
                    ? traceDetails
                    : BuildCapabilityGuardLines(log);
            }
            else
            {
                detail = log.CapabilityAdjusted
                    ? $"{GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}"
                    : "OK";

                lines = BuildCapabilityGuardLines(log);
            }

            var state =
                (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-") || log.CapabilityAdjusted
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success;

            return new NodeDecisionStepViewData
            {
                Title = "Capability",
                Detail = detail,
                State = state,
                Highlight = !string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-",
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildMemoryStep(AiExecutionLogEntry log)
        {
            var recall = log.MemoryRecall ?? MemoryRecallStats.Empty;

            if (recall.Suppressed)
            {
                return new NodeDecisionStepViewData
                {
                    Title = "Memory",
                    Detail = "本次略過記憶",
                    State = NodeDecisionStepState.Info,
                    DetailLines = new List<string>
                    {
                        string.IsNullOrWhiteSpace(recall.SuppressReason)
                            ? "本次未注入記憶。"
                            : recall.SuppressReason
                    }
                };
            }

            string detail = $"偏好 {recall.PreferenceCount}・記憶 {recall.EpisodicCount}";

            var lines = new List<string>
            {
                $"偏好（一律注入）：{recall.PreferenceCount} 條",
                $"相關記憶（召回）：{recall.EpisodicCount} 筆"
                    + (recall.EpisodicCount > 0 ? $"（Agent {recall.AgentCount}・共享 {recall.SharedCount}）" : "")
            };

            if (recall.Details != null && recall.Details.Count > 0)
            {
                lines.Add("--- 本次召回內容 ---");
                lines.AddRange(recall.Details);
            }
            else if (!recall.HasAny)
            {
                lines.Add("本次沒有可用的偏好或相關記憶。");
            }

            return new NodeDecisionStepViewData
            {
                Title = "Memory",
                Detail = detail,
                State = recall.HasAny ? NodeDecisionStepState.Success : NodeDecisionStepState.Info,
                Highlight = recall.HasAny,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildWorkspaceStep(AiExecutionLogEntry log)
        {
            var detailLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
            {
                detailLines.AddRange(
                    log.WorkspaceSummary
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()));
            }

            if (log.WorkspaceArtifactDetails != null && log.WorkspaceArtifactDetails.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifacts ---");

                detailLines.AddRange(log.WorkspaceArtifactDetails.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if ((log.WorkspaceArtifactDetails == null || log.WorkspaceArtifactDetails.Count == 0) &&
                log.WorkspaceArtifacts != null &&
                log.WorkspaceArtifacts.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifact Index ---");

                detailLines.AddRange(log.WorkspaceArtifacts
                    .Where(x => x != null)
                    .Take(20)
                    .Select(x => $"ArtifactRecord: {Safe(x.ArtifactKind)} / {Safe(x.ContentFormat)} / {(x.IsUserVisible ? "visible" : "internal")} / Type: {Safe(x.ItemType)} / Agent: {Safe(x.SourceAgentId)} / Title: {Safe(x.Title)}"));
            }

            string detail = "-";
            var state = NodeDecisionStepState.Info;

            if (detailLines.Count == 0)
            {
                detail = "No workspace artifacts";
            }
            else
            {
                var artifactCount = log.WorkspaceArtifacts?.Count ?? 0;
                if (artifactCount == 0)
                {
                    artifactCount = log.WorkspaceArtifactDetails?
                        .Count(x => x.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase)) ?? 0;
                }

                var factCount = log.WorkspaceArtifactDetails?
                    .Count(x => x.TrimStart().StartsWith("Fact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

                if (factCount == 0 && log.WorkspaceArtifacts != null)
                    factCount = log.WorkspaceArtifacts.Sum(x => x?.FactCount ?? 0);

                detail = $"Artifacts: {artifactCount}, facts: {factCount}";
                state = factCount > 0 ? NodeDecisionStepState.Success : NodeDecisionStepState.Info;
            }

            var artifactRecords = log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>();

            if (detail == "-" && artifactRecords.Count > 0)
            {
                int visible = artifactRecords.Count(x => x != null && x.IsUserVisible);
                detail = $"產出物 {artifactRecords.Count} 項（可見 {visible}）";
                state = NodeDecisionStepState.Success;
            }

            return new NodeDecisionStepViewData
            {
                Title = "Workspace",
                Detail = detail,
                State = state,
                Highlight = detailLines.Count > 0 || artifactRecords.Count > 0,
                DetailLines = detailLines,
                WorkspaceArtifacts = artifactRecords
            };
        }

        private static IReadOnlyList<string> BuildCapabilityGuardLines(AiExecutionLogEntry log)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId))
                lines.Add($"Requested: {GetModelLabel(log.CapabilityRequestedModelId)}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
                lines.Add($"Resolved: {GetModelLabel(log.CapabilityResolvedModelId)}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired))
                lines.Add($"Required: {log.CapabilityRequired}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                lines.Add($"Missing: {log.CapabilityMissing}");

            if (log.CapabilityStreamingAdjusted)
                lines.Add("Streaming adjusted: off");

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                lines.Add($"Reason: {log.CapabilityReason}");

            if (lines.Count == 0)
                lines.Add("Capability Guard: OK");

            return lines;
        }

        private static NodeDecisionStepViewData BuildFallbackStep(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            string detail = BuildFallbackDetail(log, apiFallbackUsed);
            if (string.IsNullOrWhiteSpace(detail))
                detail = "無";

            var lines = new List<string>();

            if (apiFallbackUsed)
                lines.Add("Resolver fallback: API Auto → Rules");

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                lines.Add($"Runtime Summary: {log.RuntimeFallbackSummary}");

            if (log.FallbackAttempts != null && log.FallbackAttempts.Count > 0)
            {
                foreach (var attempt in log.FallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string modelLabel = GetModelLabel(attempt.ModelId);
                    string symbol = attempt.Success ? "✅" : "❌";
                    string reason = string.IsNullOrWhiteSpace(attempt.Reason) ? "-" : attempt.Reason;
                    string error = string.IsNullOrWhiteSpace(attempt.ErrorMessage) ? "" : $" / {attempt.ErrorMessage}";

                    lines.Add($"{attempt.AttemptIndex}. {modelLabel} {symbol} / {reason}{error}");
                }
            }

            if (lines.Count == 0)
                lines.Add("No fallback used.");

            return new NodeDecisionStepViewData
            {
                Title = "Fallback",
                Detail = detail,
                State = (log.RuntimeFallbackUsed || apiFallbackUsed)
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildExecutionStep(AiExecutionLogEntry log)
        {
            string executionDetail = log.Success
                ? $"成功 / {log.DurationMs}ms"
                : $"失敗 / {Safe(log.ErrorMessage)}";

            var lines = new List<string>
            {
                $"StartedAtUtc: {log.StartedAtUtc:yyyy-MM-dd HH:mm:ss}",
                $"EndedAtUtc: {log.EndedAtUtc:yyyy-MM-dd HH:mm:ss}",
                $"Duration: {log.DurationMs} ms",
                $"Success: {log.Success}"
            };

            if (!string.IsNullOrWhiteSpace(log.ErrorMessage))
                lines.Add($"Error: {log.ErrorMessage}");

            return new NodeDecisionStepViewData
            {
                Title = "Execution",
                Detail = executionDetail,
                State = log.Success ? NodeDecisionStepState.Success : NodeDecisionStepState.Error,
                Highlight = !log.Success,
                DetailLines = lines
            };
        }

        private static string BuildTaskSummary(AiExecutionLogEntry log)
        {
            return $"{Safe(log.TaskMode)} / {log.Confidence:0.00} / {log.DurationMs}ms";
        }

        private static string BuildKeywordSummary(AiExecutionLogEntry log)
        {
            if (log?.ResolverKeywords == null || log.ResolverKeywords.Count == 0)
                return "";

            var keywords = log.ResolverKeywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keywords.Count == 0)
                return "";

            return "keywords: " + string.Join(", ", keywords);
        }

        private static string BuildCapabilityTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return "";

            return AgentCapabilityTraceFormatter.BuildSummary(log.CapabilityTrace);
        }

        private static IReadOnlyList<string> BuildCapabilityTraceDetails(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return new List<string>();

            return AgentCapabilityTraceFormatter.BuildDetailLines(log.CapabilityTrace);
        }
        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string BuildExtraSummary(AiExecutionLogEntry log)
        {
            var extraParts = new List<string>();

            string capabilityTraceSummary = BuildCapabilityTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(capabilityTraceSummary) && capabilityTraceSummary != "-")
                extraParts.Add("capability: " + capabilityTraceSummary);

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                extraParts.Add(log.CapabilityReason);

            string capabilityDetail = BuildCapabilityDetail(log);
            if (!string.IsNullOrWhiteSpace(capabilityDetail))
                extraParts.Add("capability-guard: " + capabilityDetail);

            if (!string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                extraParts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                extraParts.Add(trace);

            if (!log.Success && !string.IsNullOrWhiteSpace(log.ErrorMessage))
                extraParts.Add(log.ErrorMessage);

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
                extraParts.Add("多代理協作: " + Trim(log.WorkspaceSummary, 360));


            return extraParts.Count == 0 ? "" : string.Join(" / ", extraParts);
        }

        private static string BuildCapabilityDetail(AiExecutionLogEntry log)
        {
            if (log == null || !log.CapabilityAdjusted)
                return "";

            string requestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string resolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                parts.Add($"missing: {log.CapabilityMissing}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired) &&
                !string.Equals(log.CapabilityRequired, AiModelCapability.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"required: {log.CapabilityRequired}");
            }

            if (!string.Equals(requestedLabel, resolvedLabel, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{requestedLabel} → {resolvedLabel}");

            if (log.CapabilityStreamingAdjusted)
                parts.Add("streaming → off");

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackDetail(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            var parts = new List<string>();

            if (apiFallbackUsed)
                parts.Add("resolver fallback");

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                parts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                parts.Add(trace);

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.FallbackAttempts == null || log.FallbackAttempts.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var attempt in log.FallbackAttempts)
            {
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.ModelId))
                    continue;

                string modelLabel = GetModelLabel(attempt.ModelId);
                string symbol = attempt.Success ? "✅" : "❌";

                parts.Add($"{attempt.AttemptIndex}.{modelLabel}{symbol}");
            }

            if (parts.Count == 0)
                return "";

            return "trace: " + string.Join(" → ", parts);
        }

        private static string GetModelLabel(string? modelId)
        {
            var def = AiModelHelper.GetDefinition(modelId);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }

        private static string Safe(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? "-" : text;
        }
    }
}
