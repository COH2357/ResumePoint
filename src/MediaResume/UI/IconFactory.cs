using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MediaResume.Core;

namespace MediaResume.UI;

/// <summary>运行时生成托盘图标（避免额外资源文件依赖）。</summary>
internal static class IconFactory
{
    private static Icon? _cached;

    public static Icon Get()
    {
        if (_cached is not null) return _cached;
        _cached = Create();
        return _cached;
    }

    private static Icon Create()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using var bg = new SolidBrush(Color.FromArgb(32, 120, 200));
                g.FillEllipse(bg, 1, 1, 30, 30);

                // "续播"符号：逆时针圆弧 + 三角箭头
                using var arcPen = new Pen(Color.White, 2.6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawArc(arcPen, 8, 8, 16, 16, 40, 275);

                using var arrow = new SolidBrush(Color.White);
                g.FillPolygon(arrow, new[]
                {
                    new PointF(22.5f, 5.0f),
                    new PointF(26.5f, 10.5f),
                    new PointF(19.5f, 10.5f),
                });
            }

            var handle = bmp.GetHicon();
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}
