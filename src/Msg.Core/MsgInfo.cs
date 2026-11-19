namespace Msg.Core;

public static class MsgInfo
{
    public const string Version = "0.1.1";
}

public sealed class MsgException : Exception
{
    public MsgException() { }

    public MsgException(string message) : base(message) { }

    public MsgException(string message, Exception inner) : base(message, inner) { }
}
