namespace MuseDesktop.Core;

/// <summary>Discrete zoom steps offered by the toolbar and shortcuts.</summary>
public static class ZoomMath
{
    public static readonly IReadOnlyList<double> Steps = new[]
    {
        0.50, 0.67, 0.75, 0.80, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00,
    };

    public const double Min = 0.50;
    public const double Max = 2.00;

    /// <summary>Clamp an arbitrary factor (e.g. restored from settings) to the supported range.</summary>
    public static double Clamp(double factor)
    {
        if (double.IsNaN(factor) || factor <= 0)
            return 1.0;
        return Math.Min(Max, Math.Max(Min, factor));
    }

    /// <summary>The next step strictly above <paramref name="current"/>, or the max when already there.</summary>
    public static double StepUp(double current)
    {
        foreach (var step in Steps)
        {
            if (step > current + 0.001)
                return step;
        }
        return Max;
    }

    /// <summary>The next step strictly below <paramref name="current"/>, or the min when already there.</summary>
    public static double StepDown(double current)
    {
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < current - 0.001)
                return Steps[i];
        }
        return Min;
    }

    /// <summary>"100%", "67%", … for the toolbar label.</summary>
    public static string FormatPercent(double factor) =>
        $"{(int)Math.Round(Clamp(factor) * 100)}%";
}
