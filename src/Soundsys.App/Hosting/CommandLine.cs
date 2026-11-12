namespace Soundsys.App.Hosting;

/// <summary>soundsys.exe [--simulated] [--tray] [--restarted].</summary>
internal sealed record CommandLine(bool Simulated, bool Tray, bool Restarted, IReadOnlyList<string> Other)
{
    public const string SimulatedFlag = "--simulated";
    public const string TrayFlag = "--tray";
    public const string RestartedFlag = "--restarted";

    public static CommandLine Parse(IEnumerable<string> args)
    {
        bool simulated = false, tray = false, restarted = false;
        var other = new List<string>();
        foreach (var a in args)
        {
            if (a.Equals(SimulatedFlag, StringComparison.OrdinalIgnoreCase))
            {
                simulated = true;
            }
            else if (a.Equals(TrayFlag, StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
            }
            else if (a.Equals(RestartedFlag, StringComparison.OrdinalIgnoreCase))
            {
                restarted = true;
            }
            else
            {
                other.Add(a);
            }
        }

        return new CommandLine(simulated, tray, restarted, other);
    }

    /// <summary>Arguments for the restarted process: the same ones, with the simulated flag set as asked, shown (not tray).</summary>
    public IReadOnlyList<string> ForRestart(bool simulated)
    {
        var args = new List<string>(Other);
        if (simulated)
        {
            args.Add(SimulatedFlag);
        }

        args.Add(RestartedFlag);
        return args;
    }
}
