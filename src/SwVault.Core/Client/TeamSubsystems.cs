using System.Text.Json;
using SwVault.Core.Auth;
using SwVault.Core.Hosts;
using SwVault.Core.Util;

namespace SwVault.Core.Client;

/// <summary>A responsible engineer (RE) of a subsystem; <see cref="Approved"/> is false until an admin approves.</summary>
public sealed record SubsystemEngineer(string Login, bool Approved);

/// <summary>A subsystem of a car (e.g. "Front Suspension"), which is a folder in the vault.</summary>
public sealed record Subsystem(string Id, string Name, string Folder, string CarId, string CarName, IReadOnlyList<SubsystemEngineer> Engineers)
{
    public IEnumerable<string> ApprovedEngineers => Engineers.Where(e => e.Approved).Select(e => e.Login);

    public bool IsEngineer(string login) => Engineers.Any(e => e.Approved && string.Equals(e.Login, login, StringComparison.OrdinalIgnoreCase));

    public bool HasPendingRequest(string login) => Engineers.Any(e => !e.Approved && string.Equals(e.Login, login, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the vault path is inside this subsystem's folder.</summary>
    public bool Contains(string vaultPath) =>
        PathRules.Normalize(vaultPath).StartsWith(PathRules.Normalize(Folder) + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed record Car(string Id, string Name, string Folder, IReadOnlyList<Subsystem> Subsystems);

public sealed record LeadRequest(string Login, string FullName, string Subteam);

public sealed record EngineerRequest(string Login, string FullName, string SubsystemId, string Subsystem, string Car, string Folder);

public sealed record PendingApprovals(IReadOnlyList<LeadRequest> Leads, IReadOnlyList<EngineerRequest> Engineers)
{
    public int Count => Leads.Count + Engineers.Count;
}

public static partial class TeamInvites
{
    /// <summary>The subsystem a vault file belongs to: the one with the deepest folder holding it, or null.</summary>
    public static Subsystem? SubsystemFor(IEnumerable<Car> cars, string vaultPath) =>
        cars.SelectMany(c => c.Subsystems)
            .Where(s => s.Contains(vaultPath))
            .OrderByDescending(s => PathRules.Normalize(s.Folder).Length)
            .FirstOrDefault();

    public static async Task<IReadOnlyList<Car>> CarsAsync(string vaultUrl, Credential me, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "cars", me, null, ct).ConfigureAwait(false);
        return ParseCars(doc.RootElement);
    }

    internal static IReadOnlyList<Car> ParseCars(JsonElement root) =>
        root.TryGetProperty("cars", out var cars) && cars.ValueKind == JsonValueKind.Array
            ? cars.EnumerateArray().Select(ParseCar).ToList()
            : Array.Empty<Car>();

    private static Car ParseCar(JsonElement c)
    {
        var id = HostAdapters.GetString(c, "id") ?? "";
        var name = HostAdapters.GetString(c, "name") ?? "";
        var subsystems = c.TryGetProperty("subsystems", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(s => ParseSubsystem(s, id, name)).ToList()
            : new List<Subsystem>();
        return new Car(id, name, HostAdapters.GetString(c, "folder") ?? "", subsystems);
    }

    private static Subsystem ParseSubsystem(JsonElement s, string carId, string carName) => new(
        HostAdapters.GetString(s, "id") ?? "",
        HostAdapters.GetString(s, "name") ?? "",
        HostAdapters.GetString(s, "folder") ?? "",
        carId,
        carName,
        s.TryGetProperty("engineers", out var engineers) && engineers.ValueKind == JsonValueKind.Array
            ? engineers.EnumerateArray().Select(e => new SubsystemEngineer(
                HostAdapters.GetString(e, "login") ?? "",
                HostAdapters.GetString(e, "status") == "approved")).ToList()
            : new List<SubsystemEngineer>());

    /// <summary>Adds a car; its folder defaults to the car's name.</summary>
    public static async Task AddCarAsync(string vaultUrl, Credential me, string name, string? folder, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, "cars", me, new { name, folder }, ct).ConfigureAwait(false);
    }

    /// <summary>Adds a subsystem to a car; its folder defaults to &lt;car folder&gt;/&lt;name&gt;.</summary>
    public static async Task AddSubsystemAsync(string vaultUrl, Credential me, string carId, string name, string? folder, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, $"cars/{Uri.EscapeDataString(carId)}/subsystems", me, new { name, folder }, ct).ConfigureAwait(false);
    }

    /// <summary>Asks to be a responsible engineer. Returns true when it's approved already (admins), false when it waits.</summary>
    public static async Task<bool> ClaimEngineerAsync(string vaultUrl, Credential me, string subsystemId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, vaultUrl, $"subsystems/{Uri.EscapeDataString(subsystemId)}/engineers/me", me, new { }, ct).ConfigureAwait(false);
        return ParseSubsystem(doc.RootElement, "", "").IsEngineer(me.UserName);
    }

    /// <summary>Steps down (or withdraws a request); admins can remove anyone.</summary>
    public static async Task RemoveEngineerAsync(string vaultUrl, Credential me, string subsystemId, string login, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, vaultUrl,
            $"subsystems/{Uri.EscapeDataString(subsystemId)}/engineers/{Uri.EscapeDataString(login)}", me, null, ct).ConfigureAwait(false);
    }

    /// <summary>Admins: lead and responsible-engineer requests waiting for approval.</summary>
    public static async Task<PendingApprovals> ApprovalsAsync(string vaultUrl, Credential admin, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, vaultUrl, "approvals", admin, null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var leads = root.GetProperty("leads").EnumerateArray().Select(l => new LeadRequest(
            HostAdapters.GetString(l, "login") ?? "", HostAdapters.GetString(l, "fullName") ?? "", HostAdapters.GetString(l, "subteam") ?? "")).ToList();
        var engineers = root.GetProperty("engineers").EnumerateArray().Select(e => new EngineerRequest(
            HostAdapters.GetString(e, "login") ?? "", HostAdapters.GetString(e, "fullName") ?? "",
            HostAdapters.GetString(e, "subsystemId") ?? "", HostAdapters.GetString(e, "subsystem") ?? "",
            HostAdapters.GetString(e, "car") ?? "", HostAdapters.GetString(e, "folder") ?? "")).ToList();
        return new PendingApprovals(leads, engineers);
    }

    public static async Task DecideLeadAsync(string vaultUrl, Credential admin, string login, bool approve, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, "approvals", admin, new { kind = "lead", login, approve }, ct).ConfigureAwait(false);
    }

    public static async Task DecideEngineerAsync(string vaultUrl, Credential admin, string login, string subsystemId, bool approve, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, vaultUrl, "approvals", admin, new { kind = "engineer", login, subsystem = subsystemId, approve }, ct).ConfigureAwait(false);
    }
}
