namespace Njulf.ApiExamples;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--physics-workload") return PhysicsWorkload.Run(args);
        if (args.Contains("--help"))
        {
            Console.WriteLine("--example model|procedural|custom|content|effects|input|timing|sprites|physics-query|physics-simulation|audio [--frames N] [--validation] [--no-aa] [--async-validation] [--native-inspect] [--capture path.png] [--bindings path.json]");
            return 0;
        }
        string? previousSourceLoading = Environment.GetEnvironmentVariable("NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD");
        try
        {
            ExampleOptions options = ExampleOptions.Parse(args);
            // This executable is a development example with one authored source fixture.
            Environment.SetEnvironmentVariable("NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD", "true");
            using ExampleGame game = options.Example == "audio" ? new AudioExample(options) : options.Example.StartsWith("physics-") ? new PhysicsExample(options) : options.Example == "sprites" ? new SpritesExample(options) : options.Example == "timing" ? new TimingExample(options) : options.Example == "input" ? new InputExample(options) : options.Example == "effects" ? new EffectsExample(options) : options.Example == "content" ? new ContentExample(options) : options.Example == "custom" ? new CustomRenderingExample(options) : options.Example == "model"
                ? new ModelExample(options)
                : new ProceduralExample(options);
            game.Run();
            game.ValidateCompletion();
            if (game is ContentExample contentExample) contentExample.ValidateContentCompletion();
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
    public string? BindingsPath { get; init; }
    public static ExampleOptions Parse(string[] args)
    {
        string example = "model";
        int frames = 0;
        bool validation = false, nativeInspect = false;
        bool noAa = false;
        bool forceAsync = false;
        string? capture = null;
        string? bindings = null;
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
                case "--bindings": bindings = Path.GetFullPath(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (example is not ("model" or "procedural" or "custom" or "content" or "effects" or "input" or "timing" or "sprites" or "physics-query" or "physics-simulation" or "audio"))
            throw new ArgumentException("Unknown example; use --help to list examples.");
        if (bindings != null && example != "input") throw new ArgumentException("--bindings applies to the input example.");
        if (nativeInspect && example != "procedural")
            throw new ArgumentException("--native-inspect demonstrates the procedural mesh.");
        if (capture != null && File.Exists(capture))
            throw new ArgumentException("--capture requires a new output path so an old image cannot satisfy validation.");
        return new(example, frames, validation, nativeInspect, capture) { DisableAntialiasing = noAa, ForceAsyncValidation = forceAsync, BindingsPath = bindings };
    }

    private static string Value(string[] args, ref int index) => ++index < args.Length
        ? args[index] : throw new ArgumentException("Missing option value.");
}
