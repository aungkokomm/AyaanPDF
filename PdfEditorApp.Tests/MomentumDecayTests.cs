using PdfEditorApp.Viewport;

namespace PdfEditorApp.Tests;

public class MomentumDecayTests
{
    [Fact]
    public void Zero_velocity_is_immediately_negligible()
    {
        var decay = new MomentumDecay();
        Assert.True(decay.IsNegligible);

        var (dx, dy) = decay.Advance(1.0 / 60.0);
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Velocity_decays_towards_zero_over_time()
    {
        var decay = new MomentumDecay();
        decay.SetVelocity(1000, -1000);

        double firstSpeed = Math.Abs(decay.VelocityX);
        decay.Advance(1.0 / 60.0);
        double afterOneFrame = Math.Abs(decay.VelocityX);

        Assert.True(afterOneFrame < firstSpeed, "velocity should shrink each frame");
    }

    [Fact]
    public void Velocity_becomes_negligible_within_a_couple_seconds()
    {
        var decay = new MomentumDecay();
        decay.SetVelocity(2000, 2000);

        const double dt = 1.0 / 60.0;
        int frames = 0;
        while (!decay.IsNegligible && frames < 600) // 10s ceiling so a bug can't hang the test
        {
            decay.Advance(dt);
            frames++;
        }

        Assert.True(decay.IsNegligible, "momentum should settle out within a few seconds");
        Assert.True(frames < 300, $"expected settling well within 5s, took {frames} frames");
    }

    [Fact]
    public void Stop_zeroes_velocity_immediately()
    {
        var decay = new MomentumDecay();
        decay.SetVelocity(500, 500);

        decay.Stop();

        Assert.True(decay.IsNegligible);
    }

    [Fact]
    public void Advance_returns_delta_proportional_to_velocity_and_time()
    {
        var decay = new MomentumDecay();
        decay.SetVelocity(300, -600);

        var (dx, dy) = decay.Advance(0.1);

        Assert.Equal(30, dx, precision: 3);
        Assert.Equal(-60, dy, precision: 3);
    }
}
