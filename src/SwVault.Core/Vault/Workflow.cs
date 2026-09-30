using System.Globalization;
using SwVault.Core.Index;

namespace SwVault.Core.Vault;

public static class Revisions
{
    /// <summary>
    /// Next revision label. Alpha: "" -> A -> B ... skipping configured letters, then AA, AB...
    /// Numeric: "" -> 1 -> 2.
    /// </summary>
    public static string Next(string? current, WorkflowConfig workflow)
    {
        if (string.Equals(workflow.RevisionScheme, "numeric", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? (n + 1).ToString(CultureInfo.InvariantCulture)
                : "1";
        }

        var alphabet = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Where(l => !workflow.RevisionSkip.Any(s => string.Equals(s, l, StringComparison.OrdinalIgnoreCase)))
            .Select(l => l[0])
            .ToArray();
        if (alphabet.Length == 0) throw new InvalidOperationException("Revision scheme skips every letter.");

        if (string.IsNullOrEmpty(current)) return alphabet[0].ToString();
        var digits = current.ToUpperInvariant().Select(c => Array.IndexOf(alphabet, c)).ToList();
        if (digits.Any(d => d < 0)) return alphabet[0].ToString(); // unknown label: restart the sequence

        // Increment like a base-N counter where every position uses the allowed letters.
        var i = digits.Count - 1;
        while (i >= 0)
        {
            digits[i]++;
            if (digits[i] < alphabet.Length) break;
            digits[i] = 0;
            i--;
        }
        if (i < 0) digits.Insert(0, 0);
        return new string(digits.Select(d => alphabet[d]).ToArray());
    }
}

public sealed record TransitionCheck(
    TransitionConfig Transition,
    bool Allowed,
    string? Reason,
    string? NextRevision,
    IReadOnlyList<string> Warnings);

public static class WorkflowEngine
{
    public static string CurrentState(FileMeta? meta, VaultConfig config) =>
        string.IsNullOrEmpty(meta?.State) ? config.Workflow.InitialState : meta!.State!;

    /// <summary>Evaluates every transition defined for the file's current state.</summary>
    public static IReadOnlyList<TransitionCheck> Options(VaultConfig config, HeadFile file, string? login, HeadIndex head)
    {
        var state = CurrentState(file.Meta, config);
        return config.Workflow.Transitions
            .Where(t => t.From.Any(f => string.Equals(f, state, StringComparison.OrdinalIgnoreCase)))
            .Select(t => Check(config, file, t, login, head))
            .ToList();
    }

    public static TransitionCheck Evaluate(VaultConfig config, HeadFile file, string transitionName, string? login, HeadIndex head)
    {
        var transition = config.Workflow.Transitions.FirstOrDefault(t => string.Equals(t.Name, transitionName, StringComparison.OrdinalIgnoreCase))
            ?? throw VaultException.BadRequest($"Unknown workflow transition '{transitionName}'.");
        return Check(config, file, transition, login, head);
    }

    private static TransitionCheck Check(VaultConfig config, HeadFile file, TransitionConfig t, string? login, HeadIndex head)
    {
        var warnings = new List<string>();
        var state = CurrentState(file.Meta, config);
        string? reason = null;

        if (!t.From.Any(f => string.Equals(f, state, StringComparison.OrdinalIgnoreCase)))
            reason = $"'{t.Name}' is not available from state {state}.";
        else if (!t.Roles.Any(r => config.UserHasRole(login, r)))
            reason = $"'{t.Name}' requires role {string.Join(" or ", t.Roles)}.";

        if (reason == null && !string.Equals(t.RequireReferencesReleased, "none", StringComparison.OrdinalIgnoreCase))
        {
            var unreleased = new List<string>();
            foreach (var reference in file.Meta?.References ?? new List<ReferenceEntry>())
            {
                if (!head.Files.TryGetValue(reference.Path, out var child)) continue;
                var childState = CurrentState(child.Meta, config);
                if (!string.Equals(childState, t.To, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(childState, "Released", StringComparison.OrdinalIgnoreCase))
                    unreleased.Add($"{child.Path} ({childState})");
            }
            if (unreleased.Count > 0)
            {
                var message = "Referenced files are not released: " + string.Join(", ", unreleased);
                if (string.Equals(t.RequireReferencesReleased, "block", StringComparison.OrdinalIgnoreCase)) reason = message;
                else warnings.Add(message);
            }
        }

        var next = t.BumpRevision ? Revisions.Next(file.Meta?.Revision, config.Workflow) : null;
        return new TransitionCheck(t, reason == null, reason, next, warnings);
    }
}
