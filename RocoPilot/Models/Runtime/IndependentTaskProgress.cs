namespace RocoPilot.Models.Runtime;

public sealed record IndependentTaskProgress(string Stage, string Operation = "", string Recognition = "", string CreatureName = "");
