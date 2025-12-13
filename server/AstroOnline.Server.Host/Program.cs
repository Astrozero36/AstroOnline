using System.Diagnostics;
using AstroOnline.Server.Core.World;

namespace AstroOnline.Server.Host;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.Title = "AstroOnline.Server.Host";

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };

        try
        {
            Run(cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Run(CancellationToken token)
    {
        const int tickRate = 20;
        var tickInterval = TimeSpan.FromMilliseconds(1000.0 / tickRate);

        Console.WriteLine($"Server starting. TickRate={tickRate}Hz. Ctrl+C to stop.");

        var world = new ServerWorld();

        var sw = Stopwatch.StartNew();
        var nextTick = sw.Elapsed;

        long tick = 0;

        while (!token.IsCancellationRequested)
        {
            var now = sw.Elapsed;

            if (now < nextTick)
            {
                var sleep = nextTick - now;
                if (sleep > TimeSpan.Zero)
                    Thread.Sleep(sleep);
                continue;
            }

            if (now - nextTick > TimeSpan.FromSeconds(1))
                nextTick = now;

            tick++;

            world.Update();

            if (tick % (tickRate * 5) == 0)
            {
                Console.WriteLine(
                    $"Tick={tick} WorldTick={world.State.Tick} Uptime={sw.Elapsed:hh\\:mm\\:ss}"
                );
            }

            nextTick += tickInterval;
        }

        Console.WriteLine("Server stopping...");
        Console.WriteLine("Server stopped.");
    }
}
