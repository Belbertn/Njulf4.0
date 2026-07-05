using System;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class GlobalSdfCubicIntersectionTests
    {
        [Test]
        public void AnalyticRootMatchesBracketedTrilinearCrossing()
        {
            var random = new Random(12873);
            for (int i = 0; i < 256; i++)
            {
                double[] c = RandomCorners(random);
                Vector3 a = RandomVector(random, 0.05, 0.95);
                Vector3 b = RandomDirection(random) * (0.25 + random.NextDouble() * 1.25);
                double f0 = Evaluate(c, a);
                double f1 = Evaluate(c, a + b);
                if (Math.Sign(f0) == Math.Sign(f1) || Math.Abs(f0) < 1.0e-8 || Math.Abs(f1) < 1.0e-8)
                {
                    i--;
                    continue;
                }

                double expected = FirstSampledRoot(c, a, b);
                double actual = SolveSmallestRoot(BuildCoefficients(c, a, b), 1.0);
                Assert.That(actual, Is.EqualTo(expected).Within(1.0e-3), $"case {i}");
            }
        }

        [Test]
        public void AnalyticRootHandlesGrazingCellCrossing()
        {
            double[] c =
            {
                0.05, 0.035,
                0.055, 0.04,
                -0.01, -0.012,
                -0.008, -0.011
            };
            Vector3 a = new(0.15, 0.18, 0.02);
            Vector3 b = new(0.58, 0.54, 0.96);

            double expected = FirstSampledRoot(c, a, b);
            double actual = SolveSmallestRoot(BuildCoefficients(c, a, b), 1.0);
            Assert.That(actual, Is.EqualTo(expected).Within(1.0e-3));
        }

        private static double[] RandomCorners(Random random)
        {
            var c = new double[8];
            for (int i = 0; i < c.Length; i++)
                c[i] = random.NextDouble() * 2.0 - 1.0;
            return c;
        }

        private static Vector3 RandomVector(Random random, double min, double max)
        {
            double scale = max - min;
            return new Vector3(
                min + random.NextDouble() * scale,
                min + random.NextDouble() * scale,
                min + random.NextDouble() * scale);
        }

        private static Vector3 RandomDirection(Random random)
        {
            Vector3 v;
            do
            {
                v = new Vector3(
                    random.NextDouble() * 2.0 - 1.0,
                    random.NextDouble() * 2.0 - 1.0,
                    random.NextDouble() * 2.0 - 1.0);
            }
            while (v.Length < 1.0e-6);

            return v / v.Length;
        }

        private static double Evaluate(double[] c, Vector3 p)
        {
            double c00 = Lerp(c[0], c[1], p.X);
            double c10 = Lerp(c[2], c[3], p.X);
            double c01 = Lerp(c[4], c[5], p.X);
            double c11 = Lerp(c[6], c[7], p.X);
            return Lerp(Lerp(c00, c10, p.Y), Lerp(c01, c11, p.Y), p.Z);
        }

        private static double[] BuildCoefficients(double[] c, Vector3 aLocal, Vector3 bLocal)
        {
            double a = c[1] - c[0];
            double b = c[2] - c[0];
            double d = c[4] - c[0];
            double e = c[3] - c[1] - c[2] + c[0];
            double f = c[5] - c[1] - c[4] + c[0];
            double g = c[6] - c[2] - c[4] + c[0];
            double h = c[7] - c[3] - c[5] - c[6] + c[1] + c[2] + c[4] - c[0];
            double[] u = { aLocal.X, bLocal.X, 0.0, 0.0 };
            double[] v = { aLocal.Y, bLocal.Y, 0.0, 0.0 };
            double[] w = { aLocal.Z, bLocal.Z, 0.0, 0.0 };
            return Add(
                new[] { c[0], 0.0, 0.0, 0.0 },
                Scale(u, a),
                Scale(v, b),
                Scale(w, d),
                Scale(Mul(u, v), e),
                Scale(Mul(u, w), f),
                Scale(Mul(v, w), g),
                Scale(Mul(Mul(u, v), w), h));
        }

        private static double FirstSampledRoot(double[] c, Vector3 a, Vector3 b)
        {
            const int Samples = 16384;
            double previousT = 0.0;
            double previousF = Evaluate(c, a);
            for (int i = 1; i <= Samples; i++)
            {
                double t = i / (double)Samples;
                double f = Evaluate(c, a + b * t);
                if (Math.Sign(previousF) != Math.Sign(f))
                    return BisectionRoot(c, a, b, previousT, t);

                previousT = t;
                previousF = f;
            }

            Assert.Fail("Expected at least one sampled root bracket.");
            return double.NaN;
        }

        private static double BisectionRoot(double[] c, Vector3 a, Vector3 b, double lo, double hi)
        {
            double flo = Evaluate(c, a + b * lo);
            for (int i = 0; i < 80; i++)
            {
                double mid = (lo + hi) * 0.5;
                double fmid = Evaluate(c, a + b * mid);
                if (Math.Sign(flo) == Math.Sign(fmid))
                {
                    lo = mid;
                    flo = fmid;
                }
                else
                {
                    hi = mid;
                }
            }

            return (lo + hi) * 0.5;
        }

        private static double SolveSmallestRoot(double[] k, double tMax)
        {
            double best = double.PositiveInfinity;
            if (Math.Abs(k[3]) < 1.0e-10)
            {
                if (Math.Abs(k[2]) < 1.0e-10)
                {
                    Accept(-k[0] / k[1], tMax, ref best);
                    return best;
                }

                double disc = k[1] * k[1] - 4.0 * k[2] * k[0];
                Accept((-k[1] - Math.Sqrt(Math.Max(disc, 0.0))) / (2.0 * k[2]), tMax, ref best);
                Accept((-k[1] + Math.Sqrt(Math.Max(disc, 0.0))) / (2.0 * k[2]), tMax, ref best);
                return best;
            }

            double a = k[2] / k[3];
            double b = k[1] / k[3];
            double c = k[0] / k[3];
            double p = b - a * a / 3.0;
            double q = 2.0 * a * a * a / 27.0 - a * b / 3.0 + c;
            double halfQ = q * 0.5;
            double thirdP = p / 3.0;
            double discriminant = halfQ * halfQ + thirdP * thirdP * thirdP;
            if (discriminant > 1.0e-10)
            {
                double sqrtDisc = Math.Sqrt(discriminant);
                double u = Math.Sign(-halfQ + sqrtDisc) * Math.Pow(Math.Abs(-halfQ + sqrtDisc), 1.0 / 3.0);
                double v = Math.Sign(-halfQ - sqrtDisc) * Math.Pow(Math.Abs(-halfQ - sqrtDisc), 1.0 / 3.0);
                Accept(u + v - a / 3.0, tMax, ref best);
            }
            else
            {
                double radius = 2.0 * Math.Sqrt(Math.Max(-thirdP, 0.0));
                double denom = Math.Max(radius * radius * radius * 0.125, 1.0e-10);
                double angle = Math.Acos(Math.Clamp(-halfQ / denom, -1.0, 1.0));
                Accept(radius * Math.Cos(angle / 3.0) - a / 3.0, tMax, ref best);
                Accept(radius * Math.Cos((angle + Math.Tau) / 3.0) - a / 3.0, tMax, ref best);
                Accept(radius * Math.Cos((angle + 2.0 * Math.Tau) / 3.0) - a / 3.0, tMax, ref best);
            }

            return best;
        }

        private static void Accept(double root, double tMax, ref double best)
        {
            if (root >= -1.0e-5 && root <= tMax + 1.0e-5)
                best = Math.Min(best, Math.Clamp(root, 0.0, tMax));
        }

        private static double[] Mul(double[] lhs, double[] rhs)
        {
            return new[]
            {
                lhs[0] * rhs[0],
                lhs[0] * rhs[1] + lhs[1] * rhs[0],
                lhs[0] * rhs[2] + lhs[1] * rhs[1] + lhs[2] * rhs[0],
                lhs[0] * rhs[3] + lhs[1] * rhs[2] + lhs[2] * rhs[1] + lhs[3] * rhs[0]
            };
        }

        private static double[] Scale(double[] value, double scale) =>
            new[] { value[0] * scale, value[1] * scale, value[2] * scale, value[3] * scale };

        private static double[] Add(params double[][] values)
        {
            var result = new double[4];
            foreach (double[] value in values)
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] += value[i];
            }

            return result;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private readonly struct Vector3
        {
            public Vector3(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double X { get; }
            public double Y { get; }
            public double Z { get; }
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

            public static Vector3 operator +(Vector3 lhs, Vector3 rhs) => new(lhs.X + rhs.X, lhs.Y + rhs.Y, lhs.Z + rhs.Z);
            public static Vector3 operator *(Vector3 lhs, double rhs) => new(lhs.X * rhs, lhs.Y * rhs, lhs.Z * rhs);
            public static Vector3 operator /(Vector3 lhs, double rhs) => new(lhs.X / rhs, lhs.Y / rhs, lhs.Z / rhs);
        }
    }
}
