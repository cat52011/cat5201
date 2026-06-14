using System;

namespace test
{
    [Flags]
    public enum AiModelCapability
    {
        None = 0,
        Streaming = 1 << 0,
        Images = 1 << 1,          // 視覺輸入：可讀取圖片附件
        Files = 1 << 2,
        Search = 1 << 3,
        LongContext = 1 << 4,

        // Multi-Model v1：新增能力標籤。
        ImageGeneration = 1 << 5, // 生成圖片
        VideoGeneration = 1 << 6, // 生成影片
        Code = 1 << 7             // 程式碼任務
    }
}