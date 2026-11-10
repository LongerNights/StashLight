using System.Text;

namespace StashLight.Core;

public sealed class Query
{
    public List<string> Terms { get; } = new();        // all must match (AND), normalized
    public List<string> Excluded { get; } = new();     // none may match
    public List<string> Types { get; } = new();        // type:  -> category or record type (OR)
    public List<string> Tags { get; } = new();         // tag:   (AND)
    public List<string> InPaths { get; } = new();      // in:    path substring (AND)
    public List<string> Owners { get; } = new();       // by:    owner id/name substring (OR)
    public bool? OnlyMine { get; set; }                // is:mine
    public Source? OnlySource { get; set; }            // exact source (used by the scope dropdown)
    public string? ScopeOwner { get; set; }            // exact owner id (a group, or the local storage owner)
    public bool ExcludeGroups { get; set; }            // leave out Source.Group entries (group inventories, Local Storage); used by the "Mine" scope, which keeps what your links lead to
    public bool ExcludeLinked { get; set; }            // leave out Source.Linked entries (other people's folders reached through links)

    public bool IsEmpty => Terms.Count == 0 && Excluded.Count == 0 && Types.Count == 0
                           && Tags.Count == 0 && InPaths.Count == 0 && Owners.Count == 0 && OnlyMine == null
                           && OnlySource == null && ScopeOwner == null && !ExcludeLinked && !ExcludeGroups;

    /// <summary>Joined text terms; used for the fuzzy fallback.</summary>
    public string Text => string.Join(' ', Terms);

    public static Query Parse(string? input)
    {
        var q = new Query();
        if (string.IsNullOrWhiteSpace(input)) return q;
        foreach (var tok in Tokenize(input))
        {
            string t = tok.Text;
            if (t.Length == 0) continue;
            if (!tok.Quoted)
            {
                if (t[0] == '-' && t.Length > 1) { q.Excluded.Add(Core.Text.Normalize(t[1..])); continue; }
                int c = t.IndexOf(':');
                if (c > 0 && c < t.Length - 1)
                {
                    string key = t[..c].ToLowerInvariant(), val = Core.Text.Normalize(t[(c + 1)..]);
                    switch (key)
                    {
                        case "type": q.Types.Add(val); continue;
                        case "tag": q.Tags.Add(val); continue;
                        case "in": q.InPaths.Add(val); continue;
                        case "by": q.Owners.Add(val); continue;
                        case "is" when val == "mine": q.OnlyMine = true; continue;
                        case "is" when val == "linked": q.OnlySource = Source.Linked; continue;   // other people's folders reached through links
                        case "is" when val == "group": q.OnlySource = Source.Group; continue;     // group inventories and Local Storage
                    }
                }
            }
            q.Terms.Add(Core.Text.Normalize(t));
        }
        return q;
    }

    private readonly record struct Tok(string Text, bool Quoted);

    private static IEnumerable<Tok> Tokenize(string s)
    {
        var sb = new StringBuilder();
        bool inQuote = false, wasQuoted = false;
        foreach (char ch in s)
        {
            if (ch == '"') { if (inQuote) { yield return new Tok(sb.ToString(), true); sb.Clear(); inQuote = false; wasQuoted = false; } else { if (sb.Length > 0) { yield return new Tok(sb.ToString(), false); sb.Clear(); } inQuote = true; wasQuoted = true; } continue; }
            if (!inQuote && char.IsWhiteSpace(ch)) { if (sb.Length > 0) { yield return new Tok(sb.ToString(), false); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return new Tok(sb.ToString(), wasQuoted && !inQuote);
    }
}
