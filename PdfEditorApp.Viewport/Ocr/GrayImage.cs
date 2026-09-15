using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A page, or part of one, as 8-bit gray pixels: the form every recogniser here
/// reads, and the form the Myanmar line model was trained on.
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height)
        {
            throw new ArgumentException($"{pixels.Length} pixels do not make a {width}x{height} image.");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row by row from the top-left corner; 0 is black, 255 is white.</summary>
    public byte[] Pixels { get; }

    /// <summary>From PDFium's BGRA render, weighting the channels the way the eye does.</summary>
    public static GrayImage FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("The buffer is smaller than the image it describes.");
        }

        var gray = new byte[width * height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            gray[i] = (byte)((bgra[j] * 29 + bgra[j + 1] * 150 + bgra[j + 2] * 77) >> 8);
        }
        return new GrayImage(width, height, gray);
    }

    /// <summary>A rectangle of this image, clamped to its edges.</summary>
    public GrayImage Crop(int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);

        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(Pixels, (top + y) * Width + left, pixels, y * width, width);
        }
        return new GrayImage(width, height, pixels);
    }

    /// <summary>Otsu's threshold: the gray level that best separates ink from paper.
    /// Pixels at or below it are ink.</summary>
    public int OtsuThreshold()
    {
        var histogram = new long[256];
        foreach (byte b in Pixels) { histogram[b]++; }

        double sumAll = 0;
        for (int i = 0; i < 256; i++) { sumAll += i * (double)histogram[i]; }

        double sumBack = 0;
        double bestBetween = -1;
        long weightBack = 0;
        int threshold = 0;
        for (int t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) { continue; }
            long weightFore = Pixels.Length - weightBack;
            if (weightFore == 0) { break; }

            sumBack += t * (double)histogram[t];
            double meanBack = sumBack / weightBack;
            double meanFore = (sumAll - sumBack) / weightFore;
            double between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > bestBetween)
            {
                bestBetween = between;
                threshold = t;
            }
        }
        return threshold;
    }

    /// <summary>For each row, whether it holds any pixel at or below the threshold.</summary>
    public bool[] RowsWithInk(int threshold)
    {
        var rows = new bool[Height];
        for (int y = 0; y < Height; y++)
        {
            int offset = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (Pixels[offset + x] <= threshold)
                {
                    rows[y] = true;
                    break;
                }
            }
        }
        return rows;
    }

    /// <summary>The first and last inked columns between two rows (top inclusive,
    /// bottom exclusive), or null when the band holds no ink.</summary>
    public (int Left, int Right)? InkColumns(int top, int bottom, int threshold)
    {
        top = Math.Clamp(top, 0, Height);
        bottom = Math.Clamp(bottom, top, Height);
        int left = int.MaxValue;
        int right = -1;
        for (int y = top; y < bottom; y++)
        {
            int offset = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (Pixels[offset + x] > threshold) { continue; }
                if (x < left) { left = x; }
                if (x > right) { right = x; }
            }
        }
        return right < 0 ? null : (left, right);
    }

    /// <summary>
    /// Resized the way OpenCV's INTER_LANCZOS4 does it: eight taps a side, not
    /// widened when shrinking. The Myanmar model's training lines went through
    /// exactly that, so its input has to as well.
    /// </summary>
    public GrayImage Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "An image needs at least one pixel each way.");
        }
        if (width == Width && height == Height)
        {
            return this;
        }

        var (hIndex, hWeight) = Taps(Width, width);
        var horizontal = new float[width * Height];
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = 0; k < 8; k++) { sum += Pixels[row + hIndex[x * 8 + k]] * hWeight[x * 8 + k]; }
                horizontal[y * width + x] = sum;
            }
        }

        var (vIndex, vWeight) = Taps(Height, height);
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = 0; k < 8; k++) { sum += horizontal[vIndex[y * 8 + k] * width + x] * vWeight[y * 8 + k]; }
                pixels[y * width + x] = (byte)Math.Clamp((int)MathF.Round(sum), 0, 255);
            }
        }
        return new GrayImage(width, height, pixels);
    }

    private static (int[] Index, float[] Weight) Taps(int source, int target)
    {
        var index = new int[target * 8];
        var weight = new float[target * 8];
        double scale = (double)source / target;
        for (int i = 0; i < target; i++)
        {
            double s = (i + 0.5) * scale - 0.5;
            int floor = (int)Math.Floor(s);
            double fraction = s - floor;
            double total = 0;
            for (int k = 0; k < 8; k++)
            {
                double d = fraction + 3 - k;
                double w = Math.Abs(d) < 1e-9
                    ? 1.0
                    : 4 * Math.Sin(Math.PI * d) * Math.Sin(Math.PI * d / 4) / (Math.PI * Math.PI * d * d);
                index[i * 8 + k] = Math.Clamp(floor - 3 + k, 0, source - 1);
                weight[i * 8 + k] = (float)w;
                total += w;
            }
            for (int k = 0; k < 8; k++) { weight[i * 8 + k] = (float)(weight[i * 8 + k] / total); }
        }
        return (index, weight);
    }
}
