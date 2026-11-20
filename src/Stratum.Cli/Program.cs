using Stratum.Cli;
using Stratum.Core;

try
{
    return await Cli.RunAsync(args, Console.Out).ConfigureAwait(false);
}
catch (StratumException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
