using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiDirectionalFogCapabilities(
    bool L2IncidentRadianceSidecarAvailable,
    bool FroxelPhaseIntegrationAvailable,
    bool DirectIndirectOwnershipSeparated);

/// <summary>Orthonormal real-SH L2 incident radiance, coefficient order 0..8.</summary>
public readonly record struct SimpleDdgiL2IncidentRadiance(
    Vector3 C0,
    Vector3 C1,
    Vector3 C2,
    Vector3 C3,
    Vector3 C4,
    Vector3 C5,
    Vector3 C6,
    Vector3 C7,
    Vector3 C8)
{
    public Vector3 this[int coefficient] => coefficient switch
    {
        0 => C0,
        1 => C1,
        2 => C2,
        3 => C3,
        4 => C4,
        5 => C5,
        6 => C6,
        7 => C7,
        8 => C8,
        _ => throw new ArgumentOutOfRangeException(nameof(coefficient))
    };
}

public static class SimpleDdgiDirectionalFogExperiment
{
    public static GiExperimentAdmission EvaluateAdmission(
        bool requested,
        in SimpleDdgiDirectionalFogCapabilities capabilities,
        bool productionQualified = false,
        ulong allocatedBytes = 0UL)
    {
        if (!requested)
            return GiExperimentAdmission.Disabled("B5");
        if (!capabilities.L2IncidentRadianceSidecarAvailable)
        {
            return GiExperimentAdmission.Missing(
                "B5",
                "l2-incident-radiance-sidecar-required");
        }
        if (!capabilities.FroxelPhaseIntegrationAvailable)
        {
            return GiExperimentAdmission.Missing(
                "B5",
                "froxel-phase-consumer-required",
                capabilitySupported: true);
        }
        if (!capabilities.DirectIndirectOwnershipSeparated)
        {
            return GiExperimentAdmission.Missing(
                "B5",
                "direct-indirect-volumetric-ownership-required",
                capabilitySupported: true);
        }
        if (!productionQualified)
        {
            return new GiExperimentAdmission(
                "B5",
                true,
                true,
                false,
                GiExperimentStage.CapabilityAvailable,
                0UL,
                "directional-fog-quality-and-performance-gates-pending");
        }

        return new GiExperimentAdmission(
            "B5",
            true,
            true,
            true,
            GiExperimentStage.Active,
            allocatedBytes,
            "active");
    }

    /// <summary>
    /// Convolves incident L2 radiance with a Henyey-Greenstein phase function.
    /// In an orthonormal SH basis the degree-l transfer coefficient is g^l;
    /// this is the production math oracle for a future froxel consumer.
    /// </summary>
    public static Vector3 EvaluateScatteredRadiance(
        in SimpleDdgiL2IncidentRadiance incident,
        Vector3 towardCameraDirection,
        float anisotropy,
        float scatteringCoefficient = 1.0f)
    {
        if (!float.IsFinite(anisotropy) ||
            !float.IsFinite(scatteringCoefficient) ||
            scatteringCoefficient < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(anisotropy));
        }
        float lengthSquared = towardCameraDirection.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(towardCameraDirection));
        }

        Vector3 direction = towardCameraDirection / MathF.Sqrt(lengthSquared);
        float x = direction.X;
        float y = direction.Y;
        float z = direction.Z;
        Span<float> basis = stackalloc float[9]
        {
            0.2820947918f,
            -0.4886025119f * y,
            0.4886025119f * z,
            -0.4886025119f * x,
            1.0925484306f * x * y,
            -1.0925484306f * y * z,
            0.3153915653f * (3.0f * z * z - 1.0f),
            -1.0925484306f * x * z,
            0.5462742153f * (x * x - y * y)
        };
        float g = Math.Clamp(anisotropy, -0.95f, 0.95f);
        float g2 = g * g;
        Vector3 scattered = Vector3.Zero;
        for (int coefficient = 0; coefficient < basis.Length; coefficient++)
        {
            float phaseTransfer = coefficient == 0
                ? 1.0f
                : coefficient <= 3
                    ? g
                    : g2;
            scattered += incident[coefficient] *
                (basis[coefficient] * phaseTransfer);
        }

        return Vector3.Max(scattered * scatteringCoefficient, Vector3.Zero);
    }
}
