using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SwVault.Protocol
{
    public enum LocalState
    {
        NotLocal = 0,
        UpToDate = 1,
        Outdated = 2,
        Modified = 3,
        Conflict = 4,
        LocalOnly = 5,
        MissingLocally = 6,
        DeletedOnServer = 7,
        Ignored = 8,
    }

    public enum LockState
    {
        None = 0,
        MineHere = 1,
        MineElsewhere = 2,
        Other = 3,
    }

    public enum JobKind
    {
        GetLatest = 0,
        CheckOut = 1,
        CheckIn = 2,
        UndoCheckOut = 3,
        GetVersion = 4,
        Rollback = 5,
        Transition = 6,
        Delete = 7,
        Import = 8,
    }

    public enum JobState
    {
        Queued = 0,
        Running = 1,
        ReadyToApply = 2,
        Applying = 3,
        Completed = 4,
        Failed = 5,
        Cancelled = 6,
    }

    public enum StatusFilter
    {
        All = 0,
        MyCheckouts = 1,
        Outdated = 2,
        InReview = 3,
        LocalOnly = 4,
        CheckedOut = 5,
        Modified = 6,
    }

    [DataContract]
    public sealed class HelloRequest
    {
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "clientName")] public string ClientName { get; set; }
        [DataMember(Name = "clientVersion")] public string ClientVersion { get; set; }
        [DataMember(Name = "processId")] public int ProcessId { get; set; }
    }

    [DataContract]
    public sealed class HelloResponse
    {
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "agentVersion")] public string AgentVersion { get; set; }
        [DataMember(Name = "agentProcessId")] public int AgentProcessId { get; set; }
    }

    [DataContract]
    public sealed class VaultInfo
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "remoteUrl")] public string RemoteUrl { get; set; }
        [DataMember(Name = "localRoot")] public string LocalRoot { get; set; }
        [DataMember(Name = "userLogin", EmitDefaultValue = false)] public string UserLogin { get; set; }
        [DataMember(Name = "userDisplayName", EmitDefaultValue = false)] public string UserDisplayName { get; set; }
        [DataMember(Name = "online")] public bool Online { get; set; }
        [DataMember(Name = "lastSyncUtc", EmitDefaultValue = false)] public string LastSyncUtc { get; set; }
        [DataMember(Name = "headCommit", EmitDefaultValue = false)] public string HeadCommit { get; set; }
        [DataMember(Name = "solidworksVersion", EmitDefaultValue = false)] public string SolidworksVersion { get; set; }
        [DataMember(Name = "roles", EmitDefaultValue = false)] public string[] Roles { get; set; }
        [DataMember(Name = "lastError", EmitDefaultValue = false)] public string LastError { get; set; }
    }

    [DataContract]
    public sealed class VaultAddRequest
    {
        [DataMember(Name = "remoteUrl")] public string RemoteUrl { get; set; }
        [DataMember(Name = "localRoot", EmitDefaultValue = false)] public string LocalRoot { get; set; }
        [DataMember(Name = "userName", EmitDefaultValue = false)] public string UserName { get; set; }
        [DataMember(Name = "token", EmitDefaultValue = false)] public string Token { get; set; }
        /// <summary>Server password; the agent exchanges it for an access token and doesn't store it.</summary>
        [DataMember(Name = "password", EmitDefaultValue = false)] public string Password { get; set; }
    }

    [DataContract]
    public sealed class UiShowRequest
    {
        [DataMember(Name = "what")] public string What { get; set; }
        /// <summary>For "requestReview": the local file to review.</summary>
        [DataMember(Name = "path", EmitDefaultValue = false)] public string Path { get; set; }
    }

    /// <summary>A car subsystem (a vault folder) and its responsible engineers.</summary>
    [DataContract]
    public sealed class SubsystemInfo
    {
        [DataMember(Name = "car")] public string Car { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "folder")] public string Folder { get; set; }
        [DataMember(Name = "engineers")] public string[] Engineers { get; set; }
        [DataMember(Name = "pendingEngineers", EmitDefaultValue = false)] public string[] PendingEngineers { get; set; }
    }

    /// <summary>The team vault this install was packaged for (team.json), if any.</summary>
    [DataContract]
    public sealed class TeamInfo
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "vaultUrl")] public string VaultUrl { get; set; }
        [DataMember(Name = "localRoot")] public string LocalRoot { get; set; }
    }

    [DataContract]
    public sealed class VaultRequest
    {
        [DataMember(Name = "vaultId")] public string VaultId { get; set; }
    }

    /// <summary>Paths may be absolute local paths or vault-relative paths.</summary>
    [DataContract]
    public sealed class PathsRequest
    {
        [DataMember(Name = "vaultId", EmitDefaultValue = false)] public string VaultId { get; set; }
        [DataMember(Name = "paths")] public string[] Paths { get; set; }
    }

    [DataContract]
    public sealed class FolderRequest
    {
        [DataMember(Name = "vaultId")] public string VaultId { get; set; }
        [DataMember(Name = "folder")] public string Folder { get; set; }
    }

    [DataContract]
    public sealed class SearchRequest
    {
        [DataMember(Name = "vaultId")] public string VaultId { get; set; }
        [DataMember(Name = "query", EmitDefaultValue = false)] public string Query { get; set; }
        [DataMember(Name = "filter")] public StatusFilter Filter { get; set; }
        [DataMember(Name = "max")] public int Max { get; set; }
    }

    [DataContract]
    public sealed class FileStatusDto
    {
        [DataMember(Name = "vaultId")] public string VaultId { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "localPath")] public string LocalPath { get; set; }
        [DataMember(Name = "isFolder")] public bool IsFolder { get; set; }
        [DataMember(Name = "localState")] public LocalState LocalState { get; set; }
        [DataMember(Name = "lockState")] public LockState LockState { get; set; }
        [DataMember(Name = "lockOwner", EmitDefaultValue = false)] public string LockOwner { get; set; }
        [DataMember(Name = "lockedAt", EmitDefaultValue = false)] public string LockedAt { get; set; }
        [DataMember(Name = "localVersion")] public int LocalVersion { get; set; }
        [DataMember(Name = "serverVersion")] public int ServerVersion { get; set; }
        [DataMember(Name = "state", EmitDefaultValue = false)] public string State { get; set; }
        [DataMember(Name = "revision", EmitDefaultValue = false)] public string Revision { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "checkedInBy", EmitDefaultValue = false)] public string CheckedInBy { get; set; }
        [DataMember(Name = "checkedInAt", EmitDefaultValue = false)] public string CheckedInAt { get; set; }
        [DataMember(Name = "comment", EmitDefaultValue = false)] public string Comment { get; set; }
        [DataMember(Name = "properties", EmitDefaultValue = false)] public Dictionary<string, string> Properties { get; set; }
    }

    [DataContract]
    public sealed class VersionInfoDto
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "commit")] public string Commit { get; set; }
        [DataMember(Name = "oid")] public string Oid { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "by", EmitDefaultValue = false)] public string By { get; set; }
        [DataMember(Name = "at", EmitDefaultValue = false)] public string At { get; set; }
        [DataMember(Name = "comment", EmitDefaultValue = false)] public string Comment { get; set; }
        [DataMember(Name = "state", EmitDefaultValue = false)] public string State { get; set; }
        [DataMember(Name = "revision", EmitDefaultValue = false)] public string Revision { get; set; }
    }

    [DataContract]
    public sealed class ReferenceNodeDto
    {
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "localPath")] public string LocalPath { get; set; }
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "status", EmitDefaultValue = false)] public FileStatusDto Status { get; set; }
        [DataMember(Name = "children", EmitDefaultValue = false)] public ReferenceNodeDto[] Children { get; set; }
    }

    [DataContract]
    public sealed class TransitionOptionDto
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "to")] public string To { get; set; }
        [DataMember(Name = "bumpRevision")] public bool BumpRevision { get; set; }
        [DataMember(Name = "nextRevision", EmitDefaultValue = false)] public string NextRevision { get; set; }
        [DataMember(Name = "exports", EmitDefaultValue = false)] public string[] Exports { get; set; }
        /// <summary>Local folder where release exports (PDF/STEP/DXF) for this file go.</summary>
        [DataMember(Name = "exportFolder", EmitDefaultValue = false)] public string ExportFolder { get; set; }
        [DataMember(Name = "allowed")] public bool Allowed { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string Reason { get; set; }
        [DataMember(Name = "warnings", EmitDefaultValue = false)] public string[] Warnings { get; set; }
    }

    [DataContract]
    public sealed class LockDto
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "owner")] public string Owner { get; set; }
        [DataMember(Name = "lockedAt", EmitDefaultValue = false)] public string LockedAt { get; set; }
        [DataMember(Name = "mine")] public bool Mine { get; set; }
    }

    /// <summary>Per-file data the add-in extracts with the SOLIDWORKS API at check-in.</summary>
    [DataContract]
    public sealed class CheckInFileInfo
    {
        [DataMember(Name = "localPath")] public string LocalPath { get; set; }
        [DataMember(Name = "references", EmitDefaultValue = false)] public string[] References { get; set; }
        [DataMember(Name = "properties", EmitDefaultValue = false)] public Dictionary<string, string> Properties { get; set; }
        [DataMember(Name = "configurations", EmitDefaultValue = false)] public string[] Configurations { get; set; }
        [DataMember(Name = "swVersion", EmitDefaultValue = false)] public string SwVersion { get; set; }
    }

    [DataContract]
    public sealed class JobRequest
    {
        [DataMember(Name = "vaultId", EmitDefaultValue = false)] public string VaultId { get; set; }
        [DataMember(Name = "kind")] public JobKind Kind { get; set; }
        [DataMember(Name = "paths", EmitDefaultValue = false)] public string[] Paths { get; set; }
        [DataMember(Name = "withReferences")] public bool WithReferences { get; set; }
        [DataMember(Name = "comment", EmitDefaultValue = false)] public string Comment { get; set; }
        [DataMember(Name = "keepCheckedOut")] public bool KeepCheckedOut { get; set; }
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "asBuilt")] public bool AsBuilt { get; set; }
        [DataMember(Name = "transitionName", EmitDefaultValue = false)] public string TransitionName { get; set; }
        [DataMember(Name = "files", EmitDefaultValue = false)] public CheckInFileInfo[] Files { get; set; }
        [DataMember(Name = "force")] public bool Force { get; set; }
        /// <summary>Apply staged workspace changes without waiting for job.apply.</summary>
        [DataMember(Name = "autoApply")] public bool AutoApply { get; set; }
        /// <summary>Client correlation id, echoed in every job.updated notification for this job.</summary>
        [DataMember(Name = "tag", EmitDefaultValue = false)] public string Tag { get; set; }
    }

    [DataContract]
    public sealed class JobIdRequest
    {
        [DataMember(Name = "jobId")] public string JobId { get; set; }
        /// <summary>For job.apply: local files to leave untouched (e.g. open with unsaved changes).</summary>
        [DataMember(Name = "skipPaths", EmitDefaultValue = false)] public string[] SkipPaths { get; set; }
    }

    [DataContract]
    public sealed class JobInfo
    {
        [DataMember(Name = "jobId")] public string JobId { get; set; }
        [DataMember(Name = "vaultId", EmitDefaultValue = false)] public string VaultId { get; set; }
        [DataMember(Name = "kind")] public JobKind Kind { get; set; }
        [DataMember(Name = "state")] public JobState State { get; set; }
        [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "progress")] public double Progress { get; set; }
        /// <summary>Local files the job will overwrite or delete; the add-in releases them before job.apply.</summary>
        [DataMember(Name = "replacements", EmitDefaultValue = false)] public string[] Replacements { get; set; }
        /// <summary>Local files whose status changed once the job completed.</summary>
        [DataMember(Name = "affected", EmitDefaultValue = false)] public string[] Affected { get; set; }
        /// <summary>Local files that could not be replaced because they were in use.</summary>
        [DataMember(Name = "skipped", EmitDefaultValue = false)] public string[] Skipped { get; set; }
        [DataMember(Name = "warnings", EmitDefaultValue = false)] public string[] Warnings { get; set; }
        [DataMember(Name = "errorCode")] public int ErrorCode { get; set; }
        [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "commit", EmitDefaultValue = false)] public string Commit { get; set; }
        [DataMember(Name = "tag", EmitDefaultValue = false)] public string Tag { get; set; }
    }

    [DataContract]
    public sealed class StatusChangedNotification
    {
        [DataMember(Name = "vaultId")] public string VaultId { get; set; }
        [DataMember(Name = "paths", EmitDefaultValue = false)] public string[] Paths { get; set; }
        [DataMember(Name = "fullRefresh")] public bool FullRefresh { get; set; }
    }

    [DataContract]
    public sealed class ToastNotification
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "vaultId", EmitDefaultValue = false)] public string VaultId { get; set; }
        [DataMember(Name = "paths", EmitDefaultValue = false)] public string[] Paths { get; set; }
    }
}
