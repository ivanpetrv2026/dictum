namespace Dictum.Services;

/// <summary>
/// Keeps the last completed dictation even when SendInput was blocked (an
/// elevated window stole focus, RDP refused synthetic input, ...). The words
/// are never lost: Paste Last can type them again.
/// </summary>
public sealed class PasteLastStore
{
    private readonly object _gate = new();
    private readonly List<string> _recent = new();

    public PasteLastStore(int limit = 5)
    {
        Limit = Math.Max(1, limit);
    }

    public int Limit { get; }

    public bool LastPasteSucceeded { get; private set; } = true;

    public string? LastText
    {
        get
        {
            lock (_gate)
            {
                return _recent.Count == 0 ? null : _recent[0];
            }
        }
    }

    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.Take(Math.Min(Limit, 5)).ToArray();
            }
        }
    }

    /// <summary>
    /// A failed paste still becomes Paste Last's, exactly as an ordinary one.
    /// Empty and whitespace-only takes are ignored.
    /// </summary>
    public void Record(string text, bool pasteSucceeded)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            LastPasteSucceeded = pasteSucceeded;
            _recent.Insert(0, text);
            if (_recent.Count > Limit)
            {
                _recent.RemoveRange(Limit, _recent.Count - Limit);
            }
        }
    }

    public bool TryTake(out string text)
    {
        text = LastText ?? string.Empty;
        return text.Length > 0;
    }
}
