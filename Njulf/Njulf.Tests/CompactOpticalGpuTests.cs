using System.Diagnostics;
using NUnit.Framework;
namespace Njulf.Tests;

[TestFixture]
public sealed class CompactOpticalGpuTests
{
    private const int Width=16, Pixels=Width*Width, HalfPixels=Pixels/4;
    private const int Prefix=64+Pixels*5, NativeWords=Prefix+Pixels*4*64;
    private const int CompactWords=64+HalfPixels*64, Current=NativeWords, Previous=Current+CompactWords;
    private VulkanMaterialConformanceHarness? _gpu;
    private static uint F(float f)=>BitConverter.SingleToUInt32Bits(f);
    private static float H(uint word,bool high=false)=>(float)BitConverter.UInt16BitsToHalf((ushort)(high?word>>16:word));
    private static uint Pack(float a,float b)=>(uint)BitConverter.HalfToUInt16Bits((Half)a)|((uint)BitConverter.HalfToUInt16Bits((Half)b)<<16);
    [OneTimeSetUp] public void Setup()
    {
        DirectoryInfo? root=new(TestContext.CurrentContext.TestDirectory);
        while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Njulf.Shaders")))root=root.Parent;
        Assert.That(root,Is.Not.Null);
        string output=Path.Combine(root!.FullName,"artifacts","tests","compact-optical.spv");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var start=new ProcessStartInfo("glslangValidator"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-V","--target-env","vulkan1.1","-Os","-o",output,Path.Combine(root.FullName,"Njulf.Tests","Fixtures","Shaders","optical_compact_filter.comp")})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(30000)){process.Kill(true);Assert.Fail("Compact shader compile timeout.");}
        Assert.That(process.ExitCode,Is.Zero,stdout.Result+stderr.Result);
        if(!VulkanMaterialConformanceHarness.TryCreate(File.ReadAllBytes(output),out _gpu,out string reason))Assert.Ignore(reason);
        TestContext.Out.WriteLine(_gpu!.DeviceName);
    }
    [OneTimeTearDown]public void Dispose()=>_gpu?.Dispose();
    private static uint[] Scene(bool noisy=false)
    {
        uint[] data=new uint[Previous+CompactWords];
        data[1]=Width;data[2]=Width;data[3]=Pixels*4;data[4]=4;data[10]=Current;data[11]=Previous;
        for(int p=0;p<Pixels;p++)
        {
            int pixel=64+p*5;data[pixel]=4;
            for(int layer=0;layer<4;layer++)
            {
                int index=p*4+layer,r=Prefix+index*64;data[pixel+1+layer]=(uint)index;
                // Deliberately shuffled allocation order: nearest distances 1,2
                // are the last two records, not the first two.
                int distance=new[]{4,3,1,2}[layer];data[r]=(uint)distance;data[r+1]=7;
                data[r+4]=data[r+8]=F((p%Width)*.001f);data[r+5]=data[r+9]=F((p/Width)*.001f);
                data[r+6]=data[r+10]=F(distance);data[r+7]=F(.5f);data[r+11]=F(distance);
                float value=noisy&&distance==1?((p%Width/2+p/Width/2)%2)*4:distance*2;
                foreach(int field in new[]{12,16}){data[r+field]=data[r+field+1]=data[r+field+2]=F(value);data[r+field+3]=F(1);}
                data[r+33]=1;data[r+34]=F((p%Width+.5f)/Width);data[r+35]=F((p/Width+.5f)/Width);
            }
        }
        return data;
    }
    private uint[] Run(uint[] data,uint stage,uint step=1,uint reset=0)
    {
        data[8]=stage;data[9]=step;data[12]=reset;
        uint[] output=_gpu!.RunCompute(data,data.Length,HalfPixels);
        int current=checked((int)data[10]);
        Array.Copy(output,current,data,current,CompactWords);return data;
    }
    [Test]public void SelectionKeepsNearestTwoRegardlessOfExportOrder()
    {
        uint[] data=Run(Scene(),0);
        for(int p=0;p<HalfPixels;p++)
        {
            int r=Current+64+p*64;
            Assert.That(data[r],Is.EqualTo(1));Assert.That(data[r+32],Is.EqualTo(2));
            Assert.That(H(data[r+8]),Is.EqualTo(2));Assert.That(H(data[r+40]),Is.EqualTo(4));
        }
    }
    [Test]public void ExtendedExportFindsNearestLayersBeyondTheFirstFour()
    {
        uint[] original=Scene();
        int prefix=64+Pixels*9,nativeWords=prefix+Pixels*8*64;
        var data=new uint[nativeWords+CompactWords*2];
        Array.Copy(original,data,64);
        data[3]=Pixels*8;data[4]=8;data[10]=(uint)nativeWords;
        data[11]=(uint)(nativeWords+CompactWords);data[13]=8;
        for(int p=0;p<Pixels;p++)
        {
            int pixel=64+p*9;data[pixel]=8;
            for(int layer=0;layer<8;layer++)
            {
                int r=prefix+(p*8+layer)*64,distance=8-layer;
                data[pixel+layer+1]=(uint)(p*8+layer);
                Array.Copy(original,Prefix+(p*4)*64,data,r,64);
                data[r]=(uint)distance;data[r+6]=data[r+10]=data[r+11]=F(distance);
            }
        }
        Run(data,0);
        for(int p=0;p<HalfPixels;p++)
        {
            int r=nativeWords+64+p*64;
            Assert.That(data[r],Is.EqualTo(1));Assert.That(data[r+32],Is.EqualTo(2));
        }
    }
    [Test]public void ConstantLayersStaySeparateThroughBothSpatialPasses()
    {
        var data=Run(Scene(),0);Run(data,1,reset:1);Run(data,2);Run(data,2,2);
        for(int p=0;p<HalfPixels;p++)for(int l=0;l<2;l++)
            Assert.That(H(data[Current+64+p*64+l*32+24]),Is.EqualTo((l+1)*2).Within(.005));
    }
    [Test]public void SpatialPassesReduceNoiseWithoutMixingOtherLayer()
    {
        var data=Run(Scene(true),0);Run(data,1,reset:1);Run(data,2);Run(data,2,2);
        double error=0;
        for(int p=0;p<HalfPixels;p++) {int r=Current+64+p*64;error+=Math.Pow(H(data[r+24])-2,2);Assert.That(H(data[r+56]),Is.EqualTo(4).Within(.005));}
        Assert.That(error/HalfPixels,Is.LessThan(3.0));
    }
    [Test]public void ResetAndIdentityChangeRejectOldHistory()
    {
        var data=Run(Scene(),0);Run(data,1,reset:1);Array.Copy(data,Current,data,Previous,CompactWords);
        for(int p=0;p<HalfPixels;p++) {int r=Previous+64+p*64;data[r+8]=Pack(40,40);data[r+9]=Pack(40,16);data[r]=99;}
        Run(data,0);Run(data,1);
        for(int p=0;p<HalfPixels;p++)Assert.That(H(data[Current+64+p*64+8]),Is.EqualTo(2));
        Array.Copy(data,Current,data,Previous,CompactWords);
        for(int p=0;p<HalfPixels;p++){int r=Previous+64+p*64;data[r+8]=Pack(40,40);data[r+9]=Pack(40,16);}
        Run(data,0);Run(data,1,reset:1);
        for(int p=0;p<HalfPixels;p++)Assert.That(H(data[Current+64+p*64+8]),Is.EqualTo(2));
    }
}
