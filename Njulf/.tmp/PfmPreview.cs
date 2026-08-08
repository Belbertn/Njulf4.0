using System;
using System.IO;
using System.Text;

public static class NjulfPfmPreview
{
    private sealed class Image
    {
        public int Width;
        public int Height;
        public float[] Pixels = Array.Empty<float>();
    }

    private static string ReadLine(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        byte value;
        while ((value = reader.ReadByte()) != (byte)'\n')
        {
            if (value != (byte)'\r')
                bytes.WriteByte(value);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static Image Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, false);
        if (ReadLine(reader) != "PF")
            throw new InvalidDataException("PF image required.");
        string line;
        do
        {
            line = ReadLine(reader);
        }
        while (line.StartsWith("#", StringComparison.Ordinal));
        string[] dimensions = line.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        int width = int.Parse(dimensions[0]);
        int height = int.Parse(dimensions[1]);
        if (!ReadLine(reader).StartsWith("-", StringComparison.Ordinal))
            throw new InvalidDataException("Little-endian PFM required.");
        var pixels = new float[checked(width * height * 3)];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = reader.ReadSingle();
        return new Image { Width = width, Height = height, Pixels = pixels };
    }

    private static byte ToSrgb(float linear)
    {
        double value = Math.Max(0.0, linear) * 0.46;
        double srgb = value <= 0.0031308
            ? 12.92 * value
            : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp(
            (int)Math.Round(srgb * 255.0),
            0,
            255);
    }

    private static void WriteBmp(
        string path,
        int width,
        int height,
        Func<int, int, int, byte> channel)
    {
        int stride = checked(((width * 3) + 3) & ~3);
        int payloadBytes = checked(stride * height);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + payloadBytes);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(payloadBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        var row = new byte[stride];
        for (int bmpY = 0; bmpY < height; bmpY++)
        {
            for (int x = 0; x < width; x++)
            {
                int destination = x * 3;
                row[destination] = channel(x, bmpY, 2);
                row[destination + 1] = channel(x, bmpY, 1);
                row[destination + 2] = channel(x, bmpY, 0);
            }
            writer.Write(row);
        }
    }

    public static void Convert(string input, string output)
    {
        Image image = Read(input);
        WriteBmp(
            output,
            image.Width,
            image.Height,
            (x, y, channel) => ToSrgb(
                image.Pixels[(y * image.Width + x) * 3 + channel]));
    }

    public static void Difference(
        string referencePath,
        string candidatePath,
        string output,
        float scale)
    {
        Image reference = Read(referencePath);
        Image candidate = Read(candidatePath);
        if (reference.Width != candidate.Width ||
            reference.Height != candidate.Height)
        {
            throw new InvalidDataException("Size mismatch.");
        }
        WriteBmp(
            output,
            reference.Width,
            reference.Height,
            (x, y, channel) =>
            {
                int index =
                    (y * reference.Width + x) * 3 + channel;
                float difference = Math.Abs(
                    candidate.Pixels[index] - reference.Pixels[index]) *
                    scale;
                return ToSrgb(difference);
            });
    }

    public static string Statistics(string path)
    {
        Image image = Read(path);
        double maximum = 0.0;
        double sum = 0.0;
        int aboveOne = 0;
        int aboveTwo = 0;
        int aboveFour = 0;
        foreach (float value in image.Pixels)
        {
            maximum = Math.Max(maximum, value);
            sum += value;
            if (value > 1.0f) aboveOne++;
            if (value > 2.0f) aboveTwo++;
            if (value > 4.0f) aboveFour++;
        }
        return $"max={maximum:R};mean={sum / image.Pixels.Length:R};" +
            $"above1={aboveOne};above2={aboveTwo};above4={aboveFour};" +
            $"samples={image.Pixels.Length}";
    }
}
