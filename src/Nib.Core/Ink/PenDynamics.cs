using System;

namespace Nib.Core.Ink;

/// <summary>
/// How a pen turns pressure, tilt and speed into shape. A copy is stored with every stroke, so a
/// stroke always renders the way it looked when written even if the user later edits the pen.
/// </summary>
public sealed class PenDynamics : IEquatable<PenDynamics>
{
    public PressureCurve Curve { get; init; } = PressureCurve.Linear;
    /// <summary>When false the stroke has constant width (and constant opacity for pencil/highlighter).</summary>
    public bool PressureEnabled { get; init; } = true;
    /// <summary>Width at zero pressure as a fraction of the pen's maximum width.</summary>
    public float MinWidthRatio { get; init; } = 0.35f;
    /// <summary>0 = tilt ignored, 1 = full effect.</summary>
    public float TiltSensitivity { get; init; } = 0.6f;
    /// <summary>For devices without pressure: how strongly speed thins the line (0 = constant width).</summary>
    public float SpeedSensitivity { get; init; } = 0.5f;

    public static readonly PenDynamics Default = new();

    public bool Equals(PenDynamics? o) =>
        o is not null && Curve.Equals(o.Curve) && PressureEnabled == o.PressureEnabled &&
        MinWidthRatio == o.MinWidthRatio && TiltSensitivity == o.TiltSensitivity && SpeedSensitivity == o.SpeedSensitivity;

    public override bool Equals(object? obj) => Equals(obj as PenDynamics);
    public override int GetHashCode() => HashCode.Combine(Curve, PressureEnabled, MinWidthRatio, TiltSensitivity, SpeedSensitivity);
}

/// <summary>A pen the user keeps on the toolbar (a "favorite").</summary>
public sealed record PenPreset
{
    public PenKind Kind { get; init; } = PenKind.Ballpoint;
    /// <summary>Opaque ARGB.</summary>
    public uint Color { get; init; } = 0xFF1F1F1F;
    /// <summary>Maximum width in page units.</summary>
    public float Width { get; init; } = 2.0f;
    public PenDynamics Dynamics { get; init; } = PenDynamics.Default;
    /// <summary>Stabilizer strength 0 (raw) .. 1 (very smooth, more lag).</summary>
    public float Stabilizer { get; init; } = 0.2f;

    /// <summary>The width and feel a nib type starts with.</summary>
    public static (float Width, PenDynamics Dynamics) DefaultsFor(PenKind kind) => kind switch
    {
        PenKind.Fountain => (3.2f, new PenDynamics { MinWidthRatio = 0.25f }),
        PenKind.Pencil => (1.6f, new PenDynamics { MinWidthRatio = 0.6f, TiltSensitivity = 0.8f }),
        PenKind.Highlighter => (14f, new PenDynamics { MinWidthRatio = 0.75f, TiltSensitivity = 0.5f }),
        PenKind.Brush => (4f, new PenDynamics { MinWidthRatio = 0.08f, TiltSensitivity = 0.85f }),
        PenKind.Calligraphy => (4.5f, new PenDynamics { MinWidthRatio = 0.6f, TiltSensitivity = 0.7f }),
        PenKind.Marker => (6f, new PenDynamics { MinWidthRatio = 0.8f, TiltSensitivity = 0.6f }),
        PenKind.Watercolor => (12f, new PenDynamics { MinWidthRatio = 0.45f, TiltSensitivity = 0.85f }),
        _ => (1.8f, PenDynamics.Default),
    };

    /// <summary>
    /// Switches the nib type. Width and dynamics move to the new nib's defaults unless the user had customized
    /// them, so the change is visible straight away (a fountain nib at a ballpoint's width looks like nothing changed).
    /// </summary>
    public PenPreset WithKind(PenKind kind)
    {
        if (kind == Kind) return this;
        var (oldWidth, oldDyn) = DefaultsFor(Kind);
        var (newWidth, newDyn) = DefaultsFor(kind);
        return this with
        {
            Kind = kind,
            Width = Math.Abs(Width - oldWidth) < 0.05f ? newWidth : Width,
            Dynamics = Dynamics.Equals(oldDyn) ? newDyn : Dynamics,
        };
    }

    public static PenPreset[] DefaultFavorites() => new[]
    {
        new PenPreset { Kind = PenKind.Ballpoint, Color = 0xFF1F1F1F, Width = 1.8f },
        new PenPreset { Kind = PenKind.Ballpoint, Color = 0xFF1957C9, Width = 1.8f },
        new PenPreset { Kind = PenKind.Ballpoint, Color = 0xFFC62828, Width = 1.8f },
        new PenPreset { Kind = PenKind.Fountain, Color = 0xFF14213D, Width = 3.2f, Dynamics = new PenDynamics { MinWidthRatio = 0.25f } },
        new PenPreset { Kind = PenKind.Pencil, Color = 0xFF3A3A3A, Width = 1.6f, Dynamics = new PenDynamics { MinWidthRatio = 0.6f, TiltSensitivity = 0.8f } },
    };

    public static PenPreset DefaultHighlighter() =>
        new() { Kind = PenKind.Highlighter, Color = 0xFFFFE135, Width = 14f, Stabilizer = 0.35f, Dynamics = new PenDynamics { MinWidthRatio = 0.75f, TiltSensitivity = 0.5f } };
}
