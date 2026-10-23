using System.Runtime.InteropServices;

namespace NoPonto.ItsShadowLab;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, e => { e.Cancel = true; stop.Cancel(); });
        try { return await LabCommand.RunAsync(args, Console.Out, Console.Error, TimeProvider.System, stop.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
