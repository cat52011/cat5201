# Product Roadmap To September 2026

This project is an AI workspace product, not just a chat canvas. The September target is a commercial-grade MVP that supports multi-model routing, multi-agent orchestration, workspace artifacts, automatic downstream nodes, file generation, multimodal generation, and a controlled code-agent foundation.

## Product Target

- Multi-model: OpenAI, Claude, Perplexity, and extension points for Gemini, local models, and future providers.
- Multi-agent: general, research, file, code, workflow, presentation, media, and future specialist agents.
- Multimodal: text, attachments, image generation, video generation, PDF/document/PPT outputs.
- Workspace: all intermediate artifacts are traceable, inspectable, status-aware, and reusable.
- Workflow: one instruction can create downstream nodes and continue execution automatically.
- Code agent: keep current snapshot/diff/validation foundation; deeper Codex-like editing is a later-stage focus.
- Product UX: model cost, status, failure reason, workspace, artifacts, and generated files must be understandable to a non-developer user.

## Current Progress

Status: Phase 1.5 to Phase 2 boundary.

Done or mostly done:

- Basic node UI.
- Manual and auto model selection.
- Basic OpenAI, Claude, and Perplexity routing.
- Auto cost policy: Auto mode should not use Claude Opus or Perplexity Deep Search.
- Basic agent routing.
- Initial general, research, file, code capabilities.
- Finance research-first pipeline.
- Verified facts, source authority, quote type labeling.
- Workspace artifacts v1.
- Decision visualization v1.
- Loading spinner and timeout UX improvements.
- Code snapshot, diff, and validation foundation.

Not yet product-grade:

- Orchestrator is not yet a complete state machine.
- Automatic downstream node creation is not implemented.
- Workflow schema is not complete.
- PDF, PPT, image, and video artifact generation are not connected.
- Memory is still foundational.
- Code agent is v0.5, not Codex-like.
- Regression testing is not standardized.
- Workspace UI still needs product-level simplification.

## Ordered Work Plan

### 0. Freeze Product Direction

- [ ] Treat the project as an AI workspace product, not a simple chat tool.
- [ ] Separate MVP, v1, and v2 scope.
- [ ] Keep large-file code repair optimization for later.
- [ ] Avoid using large code-repair cases as daily regression tests.

### 1. Regression Test Checklist

- [x] Create and maintain a fixed regression checklist.
- [x] Finance test: TSM and MU short-term analysis.
- [x] General test: three-point Korean learning plan.
- [x] Attachment test: text attachment summary.
- [x] Multi-node test: previous node output feeds next node.
- [x] Model test: Auto cost protection.
- [x] Error test: timeout, canceled, no data.
- [x] Workspace test: artifact counts, types, visible/internal.
- [x] Add reusable test run template.
- [ ] Run this checklist after significant changes.

### 2. Orchestrator v1

- [x] Implement a task execution state machine. (OrchestrationStateMachine, 2026-06-12)
- [x] Define task types: research, write, summarize, generate_file, media, code, workflow.
- [x] Define initial default pipeline IDs for each task type.
- [x] Implement initial detect task -> select pipeline planning.
- [x] Move research-first pipeline formally into orchestrator. (AgentRuntime now executes from orchestrationPlan.CapabilityOrder / RequiredCapabilities / RequiresFreshFacts)
- [x] Ensure orchestrator writes workspace artifacts.
- [x] Ensure final synthesis reads workspace instead of stale context. (verified: RunFinalSynthesisAsync and final merge both consume workspace.BuildPromptBlock)
- [x] Add statuses: pending, running, success, failed, partial. (stage-level also has skipped; statuses update live in workspace orchestration artifact)

### 3. Workflow Schema

- [x] Define workflow artifact schema.
- [x] Include nodes, edges, task, agent, model, status.
- [x] Store each workflow step input and output.
- [x] Mark workflow support boundaries: canvas creation, replay, rerun, resume.
- [ ] Support workflow replay.
- [ ] Support rerunning one step.
- [ ] Support resume from failed step.
- [x] Display workflow summary in Workspace.

### 4. Automatic Downstream Nodes

- [x] Add downstream node proposal artifact without creating canvas nodes.
- [x] Add safe downstream node materialization method, not wired to automatic execution yet.
- [x] Mark materialized downstream nodes as generated/manual-run nodes.
- [ ] Automatically split large tasks into downstream nodes.
- [ ] Create nodes on the canvas.
- [ ] Create edges between generated nodes.
- [ ] Execute generated nodes in order.
- [ ] Show decision/workspace per generated node.
- [ ] Support stop, rerun, and skip.
- [ ] Example pipeline: research -> outline -> slides -> export.
- [ ] Example pipeline: search -> analysis -> report -> PDF.

### 5. Workspace and Artifact v2

- [ ] Standardize artifact schema across all capabilities.
- [ ] Add artifact status: draft, ready, validated, exported, failed.
- [ ] Add artifact source metadata: agent, model, capability, node.
- [ ] Add timestamps.
- [ ] Add artifact dependencies.
- [ ] Add artifact preview.
- [ ] Add artifact export.
- [ ] Add artifact copy where useful.
- [ ] Make Workspace look like a product surface, not a debug dump.

### 6. File Generation v1

- [x] Markdown report artifact. (MarkdownReportBuilder, 2026-06-12)
- [ ] PDF export. (deferred — needs CJK font embedding; do after md/docx)
- [x] DOCX or plain text report export. (.md/.txt via GeneratedFileWriter, UTF-8 BOM; DOCX still pending)
- [x] PPT outline artifact. (PresentationOutlinePayload, 2026-06-12)
- [ ] PPTX generation. (deferred — needs OpenXML NuGet; currently exports Marp .md deck)
- [x] Stable output file path handling. (_generated subfolder under final/file, sanitized name + timestamp)
- [x] Generated files appear as Workspace artifacts. (GeneratedFilePayload, kind=file)
- [x] Final answer can reference generated file artifacts. (answer appends 已生成檔案 + path note)

### 7. Presentation Agent

- [x] Topic -> presentation outline. (PresentationOutlineBuilder, 2026-06-12)
- [x] Outline -> slide plan. (PresentationOutlinePayload.Slides: cover/content/sources)
- [ ] Slide plan -> PPTX. (deferred — needs OpenXML NuGet; v1 exports Marp .md deck)
- [ ] Support 3, 5, and 10 slide outputs. (detects requested count from input; exact-count enforcement is v1.5)
- [ ] Support business presentation style.
- [x] Generate slides from research facts. (sources slide built from verified_facts; content from final synthesis)
- [ ] Regenerate one slide.
- [x] Export the deck. (Marp-compatible .md via GeneratedFileWriter, UTF-8 BOM)

### 8. Image Generation v1

- [ ] Image generation capability.
- [ ] Prompt -> image artifact.
- [ ] Workspace image preview.
- [ ] Image export.
- [ ] Use generated images in presentations.
- [ ] Status: queued, generating, completed, failed.
- [ ] Cost hint.

### 9. Video Generation v1

- [ ] Video generation capability.
- [ ] Prompt -> video request artifact.
- [ ] Status polling.
- [ ] Video artifact preview or link.
- [ ] Video export.
- [ ] Long-running progress UI.
- [ ] Failure and cancellation handling.

### 10. Multi-Model Expansion

- [ ] Unified model registry.
- [ ] Unified provider interface.
- [ ] Capability tags: search, vision, image, video, code, cheap, premium.
- [ ] Add models without changing orchestration core.
- [ ] Prepare extension point for Gemini and other APIs.
- [ ] UI shows model capability and cost tier.
- [ ] Fallback policy selects by capability and cost.

### 11. Memory v1

- [ ] Remember user preferences.
- [ ] Remember common formats.
- [ ] Remember common workflows.
- [ ] Remember model cost preference.
- [ ] Remember output language.
- [ ] Manual memory clear.
- [ ] Visualize when memory is used.

### 12. Code Agent v1.5

- [ ] Keep current code snapshot, diff, and validation.
- [ ] Add large-task cost/risk warning.
- [ ] Support "list bugs first, then choose one to fix".
- [ ] Support targeted context extraction.
- [ ] Support chunked analysis.
- [ ] Support patch validation.
- [ ] Later: sandbox apply.
- [ ] Later: Codex-like project editing.

### 13. Product UX

- [ ] Product-grade error messages.
- [ ] Complete loading/progress/running states.
- [ ] Token and cost display.
- [ ] Readable execution log.
- [ ] Workspace should not look like a debug dump.
- [ ] Clear node status.
- [ ] Rerun after failure.
- [ ] Clear Manual/Auto mode hints.

### 14. Demo and Delivery Polish

- [ ] Demo 1: stock analysis.
- [ ] Demo 2: learning plan PDF.
- [ ] Demo 3: research topic -> presentation.
- [ ] Demo 4: image generation -> presentation.
- [ ] Demo 5: attachment summary -> report.
- [ ] Document known limitations.
- [ ] Package demo-ready version.
- [ ] Freeze stable September build.

## Immediate Next Step

Start with `docs/REGRESSION_TEST_CHECKLIST.md`, then implement Orchestrator v1.
