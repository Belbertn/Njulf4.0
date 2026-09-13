using Njulf.Assets.Tooling;

namespace Njulf.AssetTool;

internal static class AudioCookCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("cook audio requires a source and --out <file.wav>.");
        string source = args[0], ffmpeg = "ffmpeg";
        string? output = null;
        bool mono = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                case "--ffmpeg" when i + 1 < args.Length: ffmpeg = args[++i]; break;
                case "--mono": mono = true; break;
                default: throw new ArgumentException($"Invalid audio cook argument '{args[i]}'.");
            }
        }
        if (output == null) throw new ArgumentException("cook audio requires --out <file.wav>.");
        bool changed = AudioCooker.Cook(source, output, mono, ffmpeg);
        Console.WriteLine($"{(changed ? "Cooked" : "Unchanged")} audio: {source} -> {output}");
        return 0;
    }
}
