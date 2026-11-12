using Soundsys.Diag;

// Minimal dispatcher; the hardware-check commands are added in a later phase.
if (args.Length > 0 && args[0] == ProbeCommand.Name)
    return await ProbeCommand.RunAsync(args[1..]);

Console.WriteLine("Usage: soundsys-diag probe [--out <file>] [--no-phone-start]");
return args.Length == 0 ? 0 : 2;
