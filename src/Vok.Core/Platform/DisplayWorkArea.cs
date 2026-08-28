namespace Vok.Core.Platform;

public readonly record struct DisplayWorkArea(
    int WidthPixels,
    int HeightPixels,
    uint DpiX,
    uint DpiY)
{
    private const double DefaultDpi = 96.0;

    public double WidthDip => WidthPixels * DefaultDpi / NormalizeDpi(DpiX);

    public double HeightDip => HeightPixels * DefaultDpi / NormalizeDpi(DpiY);

    private static double NormalizeDpi(uint dpi) => dpi == 0 ? DefaultDpi : dpi;
}
