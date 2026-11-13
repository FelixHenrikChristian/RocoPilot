namespace RocoPilot.Controls;

/// <summary>
/// Segoe Fluent Icons glyphs used by dialog titles. Built from code points so the
/// source stays readable (no private-use characters inline).
/// </summary>
public static class DialogGlyphs
{
    public static readonly string Add = G(0xE710);
    public static readonly string Edit = G(0xE70F);
    public static readonly string Rename = G(0xE8AC);
    public static readonly string Delete = G(0xE74D);
    public static readonly string Warning = G(0xE7BA);
    public static readonly string Info = G(0xE946);
    public static readonly string Completed = G(0xE930);
    public static readonly string Error = G(0xEA39);
    public static readonly string Download = G(0xE896);
    public static readonly string Keyboard = G(0xE765);
    public static readonly string Contact = G(0xE77B);
    public static readonly string Chart = G(0xE9D2);
    public static readonly string Sync = G(0xE895);
    public static readonly string Restart = G(0xE777);
    public static readonly string Computer = G(0xE770);
    public static readonly string Cloud = G(0xE753);

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);
}
