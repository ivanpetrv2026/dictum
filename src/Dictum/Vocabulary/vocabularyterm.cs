namespace Dictum.Vocabulary;

/// <summary>
/// One dictionary entry: the spelling that should appear in the text plus the
/// variants Whisper or the user may produce ("click house", "кликхаус", "жир").
/// </summary>
public sealed class VocabularyTerm
{
    public string Canonical { get; set; } = string.Empty;

    public List<string> Variants { get; set; } = new();
}
