using System.Text;

namespace Ledger.Core.Tests;

public class SourceTextTests
{
    /// <summary>
    /// Text saved twice as UTF-8 turns “ into "â€œ" and × into "Ã—" on screen. It happened once (1.5.1a
    /// to 2.3.2, in bill and paycheck messages), so every source file is checked for it.
    /// </summary>
    [Fact]
    public void No_source_file_has_double_encoded_characters()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        string[] extensions = [".cs", ".xaml", ".md", ".json"];
        var damaged = Directory.EnumerateFiles(dir.FullName, "*", SearchOption.AllDirectories)
            .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
            .Where(p => LooksDoubleEncoded(File.ReadAllText(p, Encoding.UTF8)))
            .Select(p => Path.GetRelativePath(dir.FullName, p))
            .ToList();
        Assert.Empty(damaged);
    }

    /// <summary>A lead byte of UTF-8 (U+00C2 to U+00F4) followed by a C1 control character: UTF-8 read as Latin-1.</summary>
    private static bool LooksDoubleEncoded(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] is >= 'Â' and <= 'ô' && text[i + 1] is >= '\u0080' and <= '\u009F')
            {
                return true;
            }
        }
        return false;
    }
}
