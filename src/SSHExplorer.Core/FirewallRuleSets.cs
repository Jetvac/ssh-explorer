namespace SSHExplorer.Core;

/// <summary>Commands that bring a server's ufw to a rule set, with what was left out and what to look at.</summary>
public sealed record RuleSetPlan(IReadOnlyList<string> Commands, IReadOnlyList<RuleDraft> Added, IReadOnlyList<UfwRule> Removed, IReadOnlyList<RuleDraft> Present, IReadOnlyList<string> Notes);

/// <summary>
/// Saving a server's rules as a reusable set and applying a set to another server. Applying adds the missing
/// rules in the set's order (an existing equal rule is kept, whatever its comment); optionally removes the server's
/// rules the set does not have. The commands then go through the usual safe apply.
/// </summary>
public static class FirewallRuleSets
{
    /// <summary>The editable form of a server rule; null for what a set cannot carry (routed, app profiles, source ports, a destination address).</summary>
    public static RuleDraft? ToDraft(UfwRule r)
    {
        if (r.Route || r.App is not null || r.FromPort is not null || r.To != "any") return null;
        var draft = new RuleDraft { Action = r.Action, Direction = r.Direction, From = r.From, Port = r.ToPort ?? "", Proto = r.Proto ?? "any", Interface = r.Interface ?? "", Comment = r.Comment ?? "" };
        return Ufw.Validate(draft) is null ? draft : null;
    }

    /// <summary>A rule set from a server's current rules; the rules it cannot carry are returned to be named.</summary>
    public static (FirewallRuleSet Set, IReadOnlyList<UfwRule> Skipped) FromSnapshot(FirewallSnapshot snapshot, string name, bool includeDefault)
    {
        var rules = new List<RuleDraft>(); var skipped = new List<UfwRule>();
        foreach (var r in snapshot.Rules) { if (ToDraft(r) is { } d) rules.Add(d); else skipped.Add(r); }
        return (new FirewallRuleSet { Name = name.Trim(), Rules = rules, DefaultIncoming = includeDefault && snapshot.DefaultIncoming is "allow" or "deny" or "reject" ? snapshot.DefaultIncoming : null, UpdatedUtc = DateTime.UtcNow }, skipped);
    }

    public static string? ValidateName(string name, IEnumerable<FirewallRuleSet> others, Guid? self = null) =>
        name.Trim().Length == 0 ? "Укажите название набора." : name.Trim().Length > 80 ? "Название — до 80 символов."
        : others.Any(o => o.Id != self && string.Equals(o.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase)) ? "Набор с таким названием уже есть." : null;

    static RuleDraft Key(RuleDraft d) => d with { Comment = "", Prepend = false, From = d.From.Trim().ToLowerInvariant(), Interface = d.Interface.Trim() };

    public static RuleSetPlan Plan(FirewallSnapshot snapshot, FirewallRuleSet set, bool removeOthers)
    {
        var existing = snapshot.Rules.Select(r => (Rule: r, Draft: ToDraft(r))).ToArray();
        var have = existing.Where(e => e.Draft is not null).Select(e => Key(e.Draft!)).ToHashSet();
        var wanted = set.Rules.Select(Key).ToHashSet();
        var commands = new List<string>(); var added = new List<RuleDraft>(); var present = new List<RuleDraft>(); var notes = new List<string>();
        var removed = removeOthers ? existing.Where(e => e.Draft is null || !wanted.Contains(Key(e.Draft))).Select(e => e.Rule).ToList() : [];
        // Remove first, then add: the set's rules end up in the set's order after whatever stays.
        foreach (var r in removed) commands.Add(Ufw.DeleteCommand(r));
        int count = snapshot.Rules.Count - removed.Count;
        foreach (var d in set.Rules)
        {
            if (Ufw.Validate(d) is { } error) { notes.Add($"Правило для порта «{d.Port}» с адреса «{d.From}» пропущено: {error}"); continue; }
            if (have.Contains(Key(d)) && !removed.Any(r => ToDraft(r) is { } rd && Key(rd) == Key(d))) { present.Add(d); continue; }
            commands.Add(Ufw.AddCommand(d with { Prepend = false }, count)); added.Add(d); count++;
        }
        if (set.DefaultIncoming is { } policy && policy != snapshot.DefaultIncoming) commands.Add(Ufw.DefaultCommand("incoming", policy));
        var hosts = set.Rules.Where(d => d.From != "any" && !d.From.Contains('/')).Select(d => d.From).Distinct().ToArray();
        if (hosts.Length > 0) notes.Add("Правила для отдельных адресов (" + string.Join(", ", hosts) + ") — проверьте, что они подходят этому серверу.");
        if (removeOthers && existing.Any(e => e.Draft is null)) notes.Add("Правила, которые набор не может описать (пересылка, профили приложений, адрес назначения), тоже будут удалены.");
        return new RuleSetPlan(commands, added, removed, present, notes);
    }
}
