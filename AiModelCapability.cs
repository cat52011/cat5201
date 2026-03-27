using System;

namespace test
{
    [Flags]
    public enum AiModelCapability
    {
        None = 0,
        Streaming = 1 << 0,
        Images = 1 << 1,
        Files = 1 << 2,
        Search = 1 << 3,
        LongContext = 1 << 4
    }
}