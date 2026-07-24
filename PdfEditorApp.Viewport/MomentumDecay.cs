using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Exponential velocity decay for drag-release momentum. No WinUI
/// dependency — unit-testable. Caller advances it once per frame and applies
/// the returned delta to pan position until <see cref="IsNegligible"/>.
/// </summary>
public sealed class MomentumDecay
{
    /// <summary>Fraction of velocity remaining after one full second — the "feel" knob.</summary>
    private const double DecayPerSecond = 0.05;

    /// <summary>Below this speed (px/sec) momentum is considered settled.</summary>
    private const double NegligibleSpeed = 5.0;

    public double VelocityX { get; private set; }
    public double VelocityY { get; private set; }

    public void SetVelocity(double velocityX, double velocityY)
    {
        VelocityX = velocityX;
        VelocityY = velocityY;
    }

    public void Stop() => SetVelocity(0, 0);

    /// <summary>Advances the decay by <paramref name="deltaSeconds"/>, returning the pan delta to apply this frame.</summary>
    public (double dx, double dy) Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0)
        {
            return (0, 0);
        }

        double dx = VelocityX * deltaSeconds;
        double dy = VelocityY * deltaSeconds;

        double decay = Math.Pow(DecayPerSecond, deltaSeconds);
        VelocityX *= decay;
        VelocityY *= decay;

        return (dx, dy);
    }

    public bool IsNegligible => Math.Abs(VelocityX) < NegligibleSpeed && Math.Abs(VelocityY) < NegligibleSpeed;
}
