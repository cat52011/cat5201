# Regression Test Checklist

This checklist is the fixed smoke/regression set for the product roadmap. Run it after meaningful changes, but do not use large code-repair tasks or unstable finance quote edge cases as daily blockers.

## Rules

- Auto mode must not use Claude Opus or Perplexity Deep Search.
- Manual mode may use expensive models only when the user explicitly selects them.
- General chat should not create unnecessary Workspace noise.
- Developer decision/workspace panels are for debugging; final user output must stay clean.
- Finance research remains a core regression test. However, do not change finance/search code unless the current task explicitly targets finance/search; record unstable provider data separately.
- Code-agent large-file repair is a conditional test only; avoid using it for every small UI/orchestration change.

## Core Tests

### Test 1: General Chat

Prompt:

```text
三點式韓文學習讀書計畫
```

Expected:

- Uses a normal chat/write route.
- Final answer is clean Traditional Chinese.
- No finance-oriented sections such as short-term trend, quote data, or verified facts.
- No internal markers, citation placeholders, or debug text.

### Test 2: Planning

Prompt:

```text
幫我規劃一個 30 天英文口說訓練計畫
```

Expected:

- Classified as planning or chat/planning.
- Produces an ordered practical plan.
- Does not invoke search unless clearly needed.
- Workspace, if shown, should be small and understandable.

### Test 3: Attachment Summary

Setup:

- Attach a readable text file.

Prompt:

```text
請用三點摘要這份附件
```

Expected:

- Uses file capability.
- Prioritizes attachment content over generic knowledge.
- Produces a concise summary.
- Workspace includes file summary / snapshot artifacts.

### Test 4: Multi-Node Chaining

Setup:

1. Node A prompt:

```text
產生一個三點式韓文學習讀書計畫
```

2. Connect Node A output to Node B.

3. Node B prompt:

```text
把上一個節點的內容整理成更適合初學者的版本
```

Expected:

- Node B uses Node A output.
- Node B does not ignore upstream context.
- Final answer has no resolver/debug markers.

### Test 5: Auto Cost Protection

Setup:

- Turn on Auto mode.

Prompt:

```text
請幫我分析這段文字並整理成重點
```

Expected:

- Auto Claude route uses Sonnet, not Opus.
- Auto Perplexity route uses Sonar, not Deep Research.
- Manual mode still allows explicit expensive model selection.

### Test 6: Workspace Sanity

Setup:

- Use Test 3 or another attachment task.

Expected:

- Workspace artifact count is reasonable.
- Artifact types are understandable.
- Visible/internal labeling is correct.
- No duplicated meaningless workflow artifacts.
- Decision panel remains useful for developer debugging.

### Test 7: Error UX

Setup:

- Trigger a canceled request, timeout, or provider failure when practical.

Expected:

- Loading indicator appears while waiting.
- Execution step shows failed/canceled state.
- Final user-facing output should not look like a raw stack trace.
- Developer decision panel may contain detailed error information.

### Test 8: Finance Research

Prompt:

```text
分析 TSM 與 MU 並給短期判斷
```

Expected:

- Uses research/search route.
- Workspace includes verified facts when search succeeds.
- Final answer clearly labels unavailable quote types as 未取得.
- Does not treat close, after-hours, pre-market, and realtime as interchangeable.
- If market data is unstable, record the issue. Do not opportunistically change finance/search code during unrelated product work.

## Conditional Tests

### Test 9: Quote Type Robustness

Prompt:

```text
給我 TSM 與 MU 的最新收盤價、盤後價、盤前價與即時價，沒有就明確標示未取得
```

Expected:

- Uses correct labels: 收盤價, 盤後價, 盤前價, 即時價.
- Missing values stay missing.
- Does not substitute one trading session for another.

### Test 10: Code-Agent Smoke Test

Setup:

- Attach a small source file, not a huge project file.

Prompt:

```text
用一句話說明這個程式在做什麼
```

Expected:

- Uses file/code snapshot if needed.
- Does not create unrelated finance/search sections.
- Does not attempt patch generation unless asked.

### Test 11: Code Diff Draft

Setup:

- Attach a small source file with an obvious issue.

Prompt:

```text
列出你看到的一個 bug，並提出修正 diff，但不要套用
```

Expected:

- Produces code snapshot and diff draft/code diff artifacts.
- Marks whether validation passed or failed.
- Does not claim an unapplied patch has already changed the file.

## Run Template

```text
Date:
Build:
Mode:
Changed area:

Core tests:
- Test 1 General Chat:
- Test 2 Planning:
- Test 3 Attachment Summary:
- Test 4 Multi-Node Chaining:
- Test 5 Auto Cost Protection:
- Test 6 Workspace Sanity:
- Test 7 Error UX:

Conditional tests:
- Test 8 Finance Research:
- Test 9 Quote Type Robustness:
- Test 10 Code-Agent Smoke:
- Test 11 Code Diff Draft:

Blocking issues:
Non-blocking issues:
Decision:
```
