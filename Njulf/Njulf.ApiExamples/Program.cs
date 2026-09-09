namespace Njulf.ApiExamples;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("--example model|procedural|custom [--frames N] [--validation] [--no-aa] [--async-validation] [--native-inspect] [--capture path.png]");
            return 0;
        }
        string? previousSourceLoading = Environment.GetEnvironmentVariable("NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD");
        try
        {
            ExampleOptions options = ExampleOptions.Parse(args);
            // This executable is a development example with one authored source fixture.
            Environment.SetEnvironmentVariable("NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD", "true");
            using ExampleGame game = options.Example == "custom" ? new CustomRenderingExample(options) : options.Example == "model"
                ? new ModelExample(options)
                : new ProceduralExample(options);
            game.Run();
            game.ValidateCompletion();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD", previousSourceLoading);
        }
    }
}

internal sealed record ExampleOptions(string Example, int Frames, bool Validation, bool NativeInspect, string? Capture)
{
    public bool DisableAntialiasing { get; init; }
    public bool ForceAsyncValidation { get; init; }
    public static ExampleOptions Parse(string[] args)
    {
        string example = "model";
        int frames = 0;
        bool validation = false, nativeInspect = false;
        bool noAa = false;
        bool forceAsync = false;
        string? capture = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--example": example = Value(args, ref i); break;
                case "--frames":
                    frames = int.Parse(Value(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                    if (frames <= 0) throw new ArgumentException("--frames must be positive.");
                    break;
                case "--validation": validation = true; break;
                case "--no-aa": noAa = true; break;
                case "--async-validation": forceAsync = true; break;
                case "--native-inspect": nativeInspect = true; break;
                case "--capture": capture = Path.GetFullPath(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (example is not ("model" or "procedural" or "custom"))
            throw new ArgumentException("--example must be model, procedural or custom.");
        if (nativeInspect && example != "procedural")
            throw new ArgumentException("--native-inspect demonstrates the procedural mesh.");
        if (capture != null && File.Exists(capture))
            throw new ArgumentException("--capture requires a new output path so an old image cannot satisfy validation.");
        return new(example, frames, validation, nativeInspect, capture) { DisableAntialiasing = noAa, ForceAsyncValidation = forceAsync };
    }

    private static string Value(string[] args, ref int index) => ++index < args.Length
        ? args[index] : throw new ArgumentException("Missing option value.");
}
