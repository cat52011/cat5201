using System;

namespace test
{
    [Flags]
    public enum AgentCapability
    {
        None = 0,

        Chat = 1 << 0,
        Research = 1 << 1,
        Translate = 1 << 2,
        Summarize = 1 << 3,
        Rewrite = 1 << 4,
        Extract = 1 << 5,
        Code = 1 << 6,

        Images = 1 << 7,
        Files = 1 << 8,
        Search = 1 << 9,
        LongContext = 1 << 10,

        Delegation = 1 << 11,
        ToolUse = 1 << 12,
        MemoryRead = 1 << 13,
        MemoryWrite = 1 << 14,

        // ===== Phase 1 Step 6 新增 =====
        FileTool = 1 << 15,
        CodeTool = 1 << 16,
        ImageTool = 1 << 17
    }
}