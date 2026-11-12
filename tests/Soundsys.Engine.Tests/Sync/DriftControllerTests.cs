using Soundsys.Engine.Sync;

namespace Soundsys.Engine.Tests.Sync;

public class DriftControllerTests
{
    private const double Rate = 48000;

    [Fact]
    public void Zero_error_gives_zero_correction()
    {
        var c = new DriftController(Rate);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, c.Update(0, 0.01));
        }
    }

    [Theory]
    [InlineData(50e-6)]
    [InlineData(-50e-6)]
    [InlineData(120e-6)]
    public void Locks_onto_constant_drift_with_negligible_steady_state_error(double drift)
    {
        // Plant: latency error grows at (drift - correction) * rate frames per second.
        var c = new DriftController(Rate);
        double error = 0, correction = 0, maxLateError = 0;
        const double dt = 0.01;
        for (var t = 0.0; t < 600; t += dt)
        {
            error += (drift - correction) * Rate * dt;
            correction = c.Update(error, dt);
            if (t > 300)
            {
                maxLateError = Math.Max(maxLateError, Math.Abs(error));
            }
        }

        Assert.True(maxLateError < 1.0, $"late error {maxLateError:F3} frames");
        Assert.Equal(drift, correction, 6);
    }

    [Fact]
    public void Error_stays_small_during_acquisition()
    {
        // ±50 ppm from a cold start must never accumulate more than 0.5 ms of error.
        var c = new DriftController(Rate);
        double error = 0, correction = 0, maxError = 0;
        for (var t = 0.0; t < 300; t += 0.01)
        {
            error += (50e-6 - correction) * Rate * 0.01;
            correction = c.Update(error, 0.01);
            maxError = Math.Max(maxError, Math.Abs(error));
        }

        Assert.True(maxError < 0.0005 * Rate, $"max error {maxError / Rate * 1000:F3} ms");
    }

    [Fact]
    public void Correction_is_clamped()
    {
        var c = new DriftController(Rate);
        double corr = 0;
        for (var i = 0; i < 1000; i++)
        {
            corr = c.Update(48000, 0.01);
        }

        Assert.Equal(DriftController.MaxCorrection, corr, 9);
        for (var i = 0; i < 1000; i++)
        {
            corr = c.Update(-48000, 0.01);
        }

        Assert.Equal(-DriftController.MaxCorrection, corr, 9);
    }

    [Fact]
    public void Reset_clears_state()
    {
        var c = new DriftController(Rate);
        for (var i = 0; i < 500; i++)
        {
            c.Update(100, 0.01);
        }

        c.Reset();
        Assert.Equal(0, c.Update(0, 0.01));
    }

    [Fact]
    public void Ignores_nonsense_time_steps()
    {
        var c = new DriftController(Rate);
        Assert.Equal(0, c.Update(0, -1));
        Assert.Equal(0, c.Update(0, 0));
        Assert.True(Math.Abs(c.Update(10, 5)) <= DriftController.MaxCorrection);
    }
}
