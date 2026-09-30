namespace SwVault.Core.Vault;

/// <summary>Contents of <c>.swvault/vault.json</c>. Shared by everyone who uses the vault.</summary>
public sealed class VaultConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Vault";

    /// <summary>
    /// Same absolute path on every PC. SOLIDWORKS stores absolute reference paths, so a common
    /// root is what lets assemblies find their parts on every machine.
    /// </summary>
    public string LocalRoot { get; set; } = @"C:\SWVault\Vault";

    /// <summary>SOLIDWORKS major version (e.g. "2025") everyone must save with. Null disables the guard.</summary>
    public string? SolidworksVersion { get; set; }

    public List<string> Ignore { get; set; } = DefaultIgnore();
    public List<string> IndexedProperties { get; set; } = new() { "PartNo", "Description", "Material", "Revision" };

    /// <summary>Folder prefixes outside the vault that references may point to without a warning (e.g. Toolbox).</summary>
    public List<string> ExternalReferenceAllowList { get; set; } = new();

    /// <summary>Role name -> user logins. "*" in a transition's roles means any signed-in user.</summary>
    public Dictionary<string, List<string>> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowConfig Workflow { get; set; } = WorkflowConfig.CreateDefault();

    /// <summary>Where release exports go. "{folder}" is the source file's folder.</summary>
    public string ReleaseExportFolder { get; set; } = "{folder}/_Released";

    public static List<string> DefaultIgnore() => new()
    {
        "~$*", "*.swbak", "*.tmp", "*.bak", "Thumbs.db", "desktop.ini", ".swvault-local/", "~*.tmp",
    };

    public static VaultConfig CreateDefault(string name, string localRoot, string adminLogin, string? solidworksVersion)
    {
        return new VaultConfig
        {
            Name = name,
            LocalRoot = localRoot,
            SolidworksVersion = solidworksVersion,
            Roles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["admin"] = new() { adminLogin },
                ["approver"] = new() { adminLogin },
            },
        };
    }

    public bool UserHasRole(string? login, string role)
    {
        if (role == "*") return !string.IsNullOrEmpty(login);
        if (string.IsNullOrEmpty(login)) return false;
        // Deserialized dictionaries lose the case-insensitive comparer, so match role names by hand.
        return Roles.Any(r => string.Equals(r.Key, role, StringComparison.OrdinalIgnoreCase)
            && r.Value.Any(m => string.Equals(m, login, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<string> RolesOf(string? login) =>
        Roles.Where(r => login != null && r.Value.Any(m => string.Equals(m, login, StringComparison.OrdinalIgnoreCase))).Select(r => r.Key);

    public bool IsAdmin(string? login) => UserHasRole(login, "admin");

    public bool IsReadOnlyState(string? state) =>
        state != null && Workflow.ReadOnlyStates.Any(s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
}

public sealed class WorkflowConfig
{
    public List<string> States { get; set; } = new();
    public string InitialState { get; set; } = "WIP";
    public List<string> ReadOnlyStates { get; set; } = new();

    /// <summary>"alpha" (A, B, ... Z, AA) or "numeric" (1, 2, 3).</summary>
    public string RevisionScheme { get; set; } = "alpha";

    /// <summary>Letters never used as revisions (commonly I and O, which look like 1 and 0).</summary>
    public List<string> RevisionSkip { get; set; } = new() { "I", "O", "Q", "S", "X", "Z" };

    public List<TransitionConfig> Transitions { get; set; } = new();

    public static WorkflowConfig CreateDefault() => new()
    {
        States = new() { "WIP", "InReview", "Released", "Obsolete" },
        InitialState = "WIP",
        ReadOnlyStates = new() { "InReview", "Released", "Obsolete" },
        Transitions = new()
        {
            new TransitionConfig { Name = "Submit for review", From = new() { "WIP" }, To = "InReview", Roles = new() { "*" } },
            new TransitionConfig
            {
                Name = "Approve", From = new() { "InReview" }, To = "Released", Roles = new() { "approver" },
                BumpRevision = true, RequireReferencesReleased = "warn", Exports = new() { "pdf" },
            },
            new TransitionConfig { Name = "Reject", From = new() { "InReview" }, To = "WIP", Roles = new() { "approver" } },
            new TransitionConfig { Name = "Change request", From = new() { "Released" }, To = "WIP", Roles = new() { "*" } },
            new TransitionConfig { Name = "Obsolete", From = new() { "WIP", "Released" }, To = "Obsolete", Roles = new() { "admin" } },
        },
    };
}

public sealed class TransitionConfig
{
    public string Name { get; set; } = "";
    public List<string> From { get; set; } = new();
    public string To { get; set; } = "";
    public List<string> Roles { get; set; } = new() { "*" };
    public bool BumpRevision { get; set; }

    /// <summary>"none", "warn" or "block": what to do when referenced files are not Released.</summary>
    public string RequireReferencesReleased { get; set; } = "none";

    /// <summary>Exports produced on this transition: "pdf" (drawings), "step" (parts/assemblies), "dxf".</summary>
    public List<string> Exports { get; set; } = new();
}
