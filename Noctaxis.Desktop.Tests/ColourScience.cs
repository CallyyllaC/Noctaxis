using Avalonia.Media;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The colour science used by the colour-vision audit: dichromacy simulation (Machado, Oliveira and
/// Fernandes 2009, severity 1.0, in linear sRGB), CIEDE2000 colour difference and WCAG 2 contrast.
/// </summary>
internal static class ColourScience
{
    private static readonly double[][] Protan = [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]];
    private static readonly double[][] Deutan = [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.011820, 0.042940, 0.968881]];
    private static readonly double[][] Tritan = [[1.255528, -0.076749, -0.178779], [-0.078411, 0.930809, 0.147602], [0.004733, 0.691367, 0.303900]];

    public static IReadOnlyList<ColourVisionMode> Deficiencies { get; } =
        [ColourVisionMode.Protanopia, ColourVisionMode.Deuteranopia, ColourVisionMode.Tritanopia];

    private static double Linear(byte channel)
    {
        var v = channel / 255d;
        return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
    }

    private static (double R, double G, double B) ToLinear(Color c) => (Linear(c.R), Linear(c.G), Linear(c.B));

    /// <summary>Linear-sRGB colour as seen with the given deficiency; <see cref="ColourVisionMode.None"/> is unchanged.</summary>
    public static (double R, double G, double B) Simulate(Color colour, ColourVisionMode mode)
    {
        var c = ToLinear(colour);
        var m = mode switch
        {
            ColourVisionMode.Protanopia => Protan,
            ColourVisionMode.Deuteranopia => Deutan,
            ColourVisionMode.Tritanopia => Tritan,
            _ => null
        };
        if (m is null) return c;
        double Row(int i) => Math.Clamp(m[i][0] * c.R + m[i][1] * c.G + m[i][2] * c.B, 0, 1);
        return (Row(0), Row(1), Row(2));
    }

    /// <summary>CIEDE2000 difference between two colours as seen with <paramref name="mode"/>.</summary>
    public static double DeltaE(Color a, Color b, ColourVisionMode mode = ColourVisionMode.None) =>
        DeltaE2000(Lab(Simulate(a, mode)), Lab(Simulate(b, mode)));

    public static double Luminance(Color c)
    {
        var (r, g, b) = ToLinear(c);
        return .2126 * r + .7152 * g + .0722 * b;
    }

    public static double Contrast(Color a, Color b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    public static double Contrast(string a, string b) => Contrast(Color.Parse(a), Color.Parse(b));

    private static (double L, double A, double B) Lab((double R, double G, double B) c)
    {
        var x = (0.4124564 * c.R + 0.3575761 * c.G + 0.1804375 * c.B) / 0.95047;
        var y = 0.2126729 * c.R + 0.7151522 * c.G + 0.0721750 * c.B;
        var z = (0.0193339 * c.R + 0.1191920 * c.G + 0.9503041 * c.B) / 1.08883;
        static double F(double t) => t > 216d / 24389 ? Math.Cbrt(t) : (24389d / 27 * t + 16) / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    private static double DeltaE2000((double L, double A, double B) c1, (double L, double A, double B) c2)
    {
        var (l1, a1, b1) = c1;
        var (l2, a2, b2) = c2;
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2), cm = (C1 + C2) / 2;
        var g = .5 * (1 - Math.Sqrt(Math.Pow(cm, 7) / (Math.Pow(cm, 7) + Math.Pow(25, 7))));
        double a1p = (1 + g) * a1, a2p = (1 + g) * a2;
        double c1p = Math.Sqrt(a1p * a1p + b1 * b1), c2p = Math.Sqrt(a2p * a2p + b2 * b2);
        static double Hue(double b, double a) { var v = Math.Atan2(b, a) * 180 / Math.PI; return v < 0 ? v + 360 : v; }
        double h1p = c1p == 0 ? 0 : Hue(b1, a1p), h2p = c2p == 0 ? 0 : Hue(b2, a2p);
        double dLp = l2 - l1, dCp = c2p - c1p;
        var dhp = c1p * c2p == 0 ? 0 : Math.Abs(h2p - h1p) <= 180 ? h2p - h1p : h2p - h1p > 180 ? h2p - h1p - 360 : h2p - h1p + 360;
        var dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp * Math.PI / 360);
        double lpm = (l1 + l2) / 2, cpm = (c1p + c2p) / 2;
        var hpm = c1p * c2p == 0 ? h1p + h2p
            : Math.Abs(h1p - h2p) <= 180 ? (h1p + h2p) / 2
            : h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
        var t = 1 - .17 * Math.Cos((hpm - 30) * Math.PI / 180) + .24 * Math.Cos(2 * hpm * Math.PI / 180)
                + .32 * Math.Cos((3 * hpm + 6) * Math.PI / 180) - .20 * Math.Cos((4 * hpm - 63) * Math.PI / 180);
        var dTheta = 30 * Math.Exp(-Math.Pow((hpm - 275) / 25, 2));
        var rc = 2 * Math.Sqrt(Math.Pow(cpm, 7) / (Math.Pow(cpm, 7) + Math.Pow(25, 7)));
        var sl = 1 + .015 * Math.Pow(lpm - 50, 2) / Math.Sqrt(20 + Math.Pow(lpm - 50, 2));
        var sc = 1 + .045 * cpm;
        var sh = 1 + .015 * cpm * t;
        var rt = -Math.Sin(2 * dTheta * Math.PI / 180) * rc;
        return Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) + rt * (dCp / sc) * (dHp / sh));
    }
}
