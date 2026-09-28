if (args.Length > 0 && args[0] == "--preview")
{
    if (args.Length < 2) throw new ArgumentException("--preview output.png [ticks] [population] [width] [height] [scale] [lens]");
    using var preview = new Hollowbound.Game1();
    preview.ConfigurePreview(args[1], args.Length > 2 ? int.Parse(args[2]) : 2000,
        args.Length > 3 ? int.Parse(args[3]) : 80, args.Length > 4 ? int.Parse(args[4]) : 1600,
        args.Length > 5 ? int.Parse(args[5]) : 900,
        args.Length > 6 ? float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 1f,
        args.Length > 7 ? int.Parse(args[7]) : 0);
    preview.Run();
}
else if (args.Length > 0 && args[0] == "--playtest")
{
    using var game = new Hollowbound.Game1();
    game.ConfigurePlaytest();
    game.Run();
}
else if (args.Length > 0 && args[0] == "--benchmark")
{
    int seed = args.Length > 1 ? int.Parse(args[1]) : Environment.TickCount;
    int targetTicks = args.Length > 2 ? int.Parse(args[2]) : 100000;
    float timeScale = args.Length > 3 ? float.Parse(args[3]) : 500f;
    int initialPopulation = args.Length > 4 ? int.Parse(args[4]) : 2;
    bool lodEnabled = args.Length <= 5 || !string.Equals(args[5], "lod=off", StringComparison.OrdinalIgnoreCase);
    Hollowbound.Benchmark.Run(seed, targetTicks, timeScale, headless: true, initialPopulation: initialPopulation, lodEnabled: lodEnabled);
}
else if (args.Length > 0 && args[0] == "--benchmark-compare")
{
    int seed = args.Length > 1 ? int.Parse(args[1]) : 12345;
    int targetTicks = args.Length > 2 ? int.Parse(args[2]) : 10000;
    float timeScale = args.Length > 3 ? float.Parse(args[3]) : 500f;
    int initialPopulation = args.Length > 4 ? int.Parse(args[4]) : 1000;
    Hollowbound.Benchmark.CompareLod(seed, targetTicks, timeScale, initialPopulation);
}
else if (args.Length > 0 && args[0] == "--deterministic-test")
{
    // Compatibility alias: the old loose ±2-population test is retired.
    // The canonical strict bit-exact check now lives in the self-test suite.
    Console.WriteLine("[deprecated] --deterministic-test is an alias for the strict 'continuation' scenario of --self-test.");
    Environment.ExitCode = Hollowbound.Simulation.SimulationRegressionRunner.Run(new[] { "continuation" });
}
else if (args.Length > 0 && args[0] == "--headless")
{
    Hollowbound.Headless.HeadlessRunner.RunFromArgs(args[1..]);
}
else if (args.Length > 0 && args[0] == "--load")
{
    string path = args.Length > 1 ? args[1] : Hollowbound.Simulation.WorldSaveService.DefaultPath;
    int ticks = args.Length > 2 ? int.Parse(args[2]) : 1000;
    float timeScale = args.Length > 3 ? float.Parse(args[3]) : 1f;
    Hollowbound.Headless.HeadlessRunner.LoadAndAdvance(path, ticks, timeScale);
}
else if (args.Length > 0 && args[0] == "--self-test")
{
    Environment.ExitCode = Hollowbound.Simulation.SimulationRegressionRunner.Run(args.Length > 1 ? args[1..] : Array.Empty<string>());
}
else if (args.Length > 0 && args[0] == "--analyze-log")
{
    Environment.ExitCode = Hollowbound.AnalyticsReport.Run(args[1..]);
}
else if (args.Length > 0 && args[0] == "--test-camera-layout")
{
    Hollowbound.Simulation.Tests.CameraLayoutTests.RunAll();
}
else
{
    using var game = new Hollowbound.Game1();
    game.Run();
}
