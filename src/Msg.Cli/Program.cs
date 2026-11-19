using Msg.Cli;
using Msg.Core;

try
{
    return Cli.Run(args, Console.Out);
}
catch (MsgException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
