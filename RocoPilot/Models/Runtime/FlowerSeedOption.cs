namespace RocoPilot.Models.Runtime;

public sealed record FlowerSeedOption(int Number, string Name)
{
    public string DisplayName => $"{Number}. {(string.IsNullOrWhiteSpace(Name) ? "未识别名称" : Name)}";
}
