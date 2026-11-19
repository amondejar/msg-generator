using Msg.Cli;
using Msg.Core;

try
{
    return await Cli.RunAsync(args, Console.Out).ConfigureAwait(false);
}
catch (MsgException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
