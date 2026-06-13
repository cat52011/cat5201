namespace test
{
    /// <summary>
    /// Memory v1：個人化設定清單的單列繫結模型——顯示整句偏好原文 + 用於刪除的 PreferenceKey。
    /// </summary>
    public sealed class PreferenceView
    {
        public string Display { get; init; } = "";
        public string Key { get; init; } = "";
    }
}
