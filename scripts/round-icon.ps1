param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [double]$Ratio = 0.21
)

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class IconRounder
{
    public static void Round(string source, string target, double ratio)
    {
        using (var raw = Image.FromFile(source))
        {
            int w = raw.Width;
            int h = raw.Height;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    float d = (float)(Math.Min(w, h) * ratio * 2);
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(0, 0, d, d, 180, 90);
                        path.AddArc(w - d, 0, d, d, 270, 90);
                        path.AddArc(w - d, h - d, d, d, 0, 90);
                        path.AddArc(0, h - d, d, d, 90, 90);
                        path.CloseFigure();
                        g.SetClip(path);
                    }

                    g.DrawImage(raw, new Rectangle(0, 0, w, h));
                }
                bmp.Save(target, ImageFormat.Png);
            }
        }
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing
[IconRounder]::Round($Source, $Target, $Ratio)
Write-Output ("rounded {0} -> {1} ({2:N0} bytes, ratio {3})" -f $Source, $Target, (Get-Item $Target).Length, $Ratio)
