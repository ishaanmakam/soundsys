namespace Soundsys.Engine.Calibration;

/// <summary>In-place iterative radix-2 complex FFT (double precision). Used offline by calibration, never on RT threads.</summary>
public static class Fft
{
    public static int NextPowerOfTwo(int n)
    {
        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    /// <summary>Forward (inverse = false) or inverse (scaled by 1/N) transform of (re, im).</summary>
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("Length must be a power of two and equal for both arrays.");
        }

        // Bit reversal.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            var half = len / 2;
            for (var i = 0; i < n; i += len)
            {
                double cRe = 1, cIm = 0;
                for (var k = 0; k < half; k++)
                {
                    var a = i + k;
                    var b = a + half;
                    var tRe = (re[b] * cRe) - (im[b] * cIm);
                    var tIm = (re[b] * cIm) + (im[b] * cRe);
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    var nRe = (cRe * wRe) - (cIm * wIm);
                    cIm = (cRe * wIm) + (cIm * wRe);
                    cRe = nRe;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
        }
    }
}
