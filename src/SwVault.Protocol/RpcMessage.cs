using System.Runtime.Serialization;

namespace SwVault.Protocol
{
    /// <summary>
    /// One line on the agent pipe. Requests carry Id + Method, responses carry Id (+ Error),
    /// notifications carry Method with Id = 0. Payloads are nested JSON strings so the
    /// envelope never needs polymorphic serialization.
    /// </summary>
    [DataContract]
    public sealed class RpcMessage
    {
        [DataMember(Name = "id", EmitDefaultValue = false)] public long Id { get; set; }
        [DataMember(Name = "method", EmitDefaultValue = false)] public string Method { get; set; }
        [DataMember(Name = "payload", EmitDefaultValue = false)] public string Payload { get; set; }
        [DataMember(Name = "error", EmitDefaultValue = false)] public RpcError Error { get; set; }

        public bool IsRequest => Id != 0 && Method != null;
        public bool IsResponse => Id != 0 && Method == null;
        public bool IsNotification => Id == 0 && Method != null;
    }

    [DataContract]
    public sealed class RpcError
    {
        [DataMember(Name = "code")] public int Code { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "data", EmitDefaultValue = false)] public string Data { get; set; }
    }

    public static class ErrorCodes
    {
        public const int BadRequest = 400;
        public const int Unauthorized = 401;
        public const int Forbidden = 403;
        public const int NotFound = 404;
        public const int Conflict = 409;
        public const int VersionMismatch = 426;
        public const int Internal = 500;
        public const int Offline = 503;
    }
}
