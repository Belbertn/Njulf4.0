using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Njulf.Rendering.Data;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AntiAliasingShaderLayoutTests
{
    [TestCase("taa_resolve.frag")]
    [TestCase("smaa_edge.frag")]
    [TestCase("smaa_blend_weight.frag")]
    [TestCase("smaa_neighborhood.frag")]
    [TestCase("fxaa.frag")]
    [TestCase("effect_present.frag")]
    public void CompiledPushMembersMatchCpuLayout(string shader)
    {
        using Stream stream = typeof(ShaderLibrary).Assembly
            .GetManifestResourceStream("Njulf.Shaders." + shader)!;
        Assert.That(stream, Is.Not.Null, shader);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        byte[] code = bytes.ToArray();
        var names = new Dictionary<uint, string>();
        var members = new Dictionary<(uint Type, uint Member), string>();
        var offsets = new Dictionary<(uint Type, uint Member), uint>();
        uint Word(int word) => BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(word * 4, 4));
        string Name(int word, int count) => Encoding.UTF8.GetString(code, word * 4, count * 4).TrimEnd('\0');
        for (int cursor = 5; cursor < code.Length / 4;)
        {
            uint instruction = Word(cursor);
            int count = (int)(instruction >> 16);
            Assert.That(count, Is.GreaterThan(0));
            switch (instruction & 0xffff)
            {
                case 5: // OpName
                    names[Word(cursor + 1)] = Name(cursor + 2, count - 2);
                    break;
                case 6: // OpMemberName
                    members[(Word(cursor + 1), Word(cursor + 2))] = Name(cursor + 3, count - 3);
                    break;
                case 72 when Word(cursor + 3) == 35: // OpMemberDecorate Offset
                    offsets[(Word(cursor + 1), Word(cursor + 2))] = Word(cursor + 4);
                    break;
            }
            cursor += count;
        }
        uint block = names.Single(pair => pair.Value == "AntiAliasingPushBlock").Key;
        var shaderMembers = members.Where(pair => pair.Key.Type == block).ToArray();
        Assert.That(shaderMembers, Has.Length.EqualTo(typeof(GPUAntiAliasingPushConstants).GetFields().Length));
        foreach (var member in shaderMembers)
        {
            int cpuOffset = Marshal.OffsetOf<GPUAntiAliasingPushConstants>(member.Value).ToInt32();
            Assert.That(offsets[member.Key], Is.EqualTo(cpuOffset), $"{shader}: {member.Value}");
            Type fieldType = typeof(GPUAntiAliasingPushConstants).GetField(member.Value)!.FieldType;
            Assert.That(offsets[member.Key] + Marshal.SizeOf(fieldType),
                Is.LessThanOrEqualTo(Marshal.SizeOf<GPUAntiAliasingPushConstants>()), member.Value);
        }
    }
}
