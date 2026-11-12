using System.Reflection;
using System.Text;
using Soundsys.Diag;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0] == ProbeCommand.Name)
{
    return await ProbeCommand.RunAsync(args[1..]);
}

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(RunCommand.Usage);
    return 0;
}

return await RunCommand.RunAsync(args);

namespace Soundsys.Diag
{
    internal static partial class Program
    {
        public static string Version =>
            typeof(RunCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    }
}
