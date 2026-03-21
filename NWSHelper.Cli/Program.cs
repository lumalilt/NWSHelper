using System.CommandLine;
using NWSHelper.Cli.Commands;

var root = new RootCommand("NWS Helper CLI - boundary-based address extraction and merge");
root.AddCommand(ExtractCommand.Create());

return await root.InvokeAsync(args);

