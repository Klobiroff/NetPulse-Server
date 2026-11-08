using Microsoft.Extensions.Hosting;
using NetPulse.Server.Hosting;
using NetPulse.Server.Security;

if (args.Length > 0 && string.Equals(args[0], "keygen", StringComparison.OrdinalIgnoreCase))
{
    return KeyGenerator.Run(args[1..], Console.Out, Console.Error);
}

await ServerHost.Build(args).RunAsync();
return 0;
