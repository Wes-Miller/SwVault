using SwVault.Protocol;

namespace SwVault.Core;

/// <summary>
/// An expected, user-facing failure (lock conflict, missing sign-in, invalid transition...).
/// <see cref="Code"/> uses the HTTP-like values from <see cref="ErrorCodes"/>.
/// </summary>
public class VaultException : Exception
{
    public int Code { get; }

    public VaultException(int code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public static VaultException Conflict(string message) => new(ErrorCodes.Conflict, message);
    public static VaultException BadRequest(string message) => new(ErrorCodes.BadRequest, message);
    public static VaultException NotFound(string message) => new(ErrorCodes.NotFound, message);
    public static VaultException Forbidden(string message) => new(ErrorCodes.Forbidden, message);
    public static VaultException Unauthorized(string message) => new(ErrorCodes.Unauthorized, message);
    public static VaultException Offline(string message, Exception? inner = null) => new(ErrorCodes.Offline, message, inner);
}
