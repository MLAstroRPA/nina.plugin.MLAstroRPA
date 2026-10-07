using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class TextSpec
{
    public string Label = "";
    public double X, Y, FontSize = 16;
    public string Family = "Arial";
    public int Weight = 400;
}

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: mlastro-iconbuild <in.svg> <out.xaml> [out.png]");
            return 2;
        }
        string svgPath = args[0];
        string outXaml = args[1];
        string outPng = args.Length > 2 ? args[2] : null;

        string svg = File.ReadAllText(svgPath);

        // strip XML comments FIRST - a comment like "<!-- ... <text>★</text> ... -->" must
        // NOT be picked up as a real glyph by the regexes below.
        svg = Regex.Replace(svg, "<!--.*?-->", "", RegexOptions.Singleline);

        // ---------- 1) optional <path> elements (kept for backward compat) ----------
        var pathFigures = new List<string>();
        foreach (Match m in Regex.Matches(svg, "<path\\b[^>]*?d=\"([^\"]*)\"[^>]*?(?:/>|</path>)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            pathFigures.Add(m.Groups[1].Value.Trim());

        // ---------- 2) ALL <text> elements (each becomes its own glyph geometry) ----------
        var texts = new List<TextSpec>();
        foreach (Match tm in Regex.Matches(svg, "<text\\b(?<attrs>[^>]*)>(?<body>.*?)</text>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = tm.Groups["attrs"].Value;
            string body = Regex.Replace(tm.Groups["body"].Value.Trim(), "<[^>]+>", "");
            if (string.IsNullOrEmpty(body)) continue;

            var spec = new TextSpec { Label = body };
            spec.X = AttrDouble(attrs, "x", 0);
            spec.Y = AttrDouble(attrs, "y", 0);
            string style = Attr(attrs, "style", "");
            double fs = StyleDouble(style, "font-size", double.NaN);
            if (!double.IsNaN(fs)) spec.FontSize = fs;
            string fam = Style(style, "font-family", null);
            if (fam != null) spec.Family = fam.Trim('\'', '"').Split(',')[0].Trim();
            string w = Style(style, "font-weight", "normal").ToLowerInvariant();
            switch (w)
            {
                case "bold": case "700": spec.Weight = 700; break;
                case "bolder": case "800": spec.Weight = 800; break;
                case "900": case "black": case "heavy": spec.Weight = 900; break;
                case "600": case "semibold": case "demi": spec.Weight = 600; break;
            }
            texts.Add(spec);
        }

        if (pathFigures.Count == 0 && texts.Count == 0)
        {
            Console.Error.WriteLine("no <path> or <text> found");
            return 3;
        }

        // ---------- 3) build all geometries ----------
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var fig in pathFigures)
        {
            var pg = ParsePathFigures(fig);
            pg.FillRule = FillRule.Nonzero;
            group.Children.Add(pg);
        }

        foreach (var t in texts)
        {
            Geometry g = BuildGlyph(t);
            if (g == null) continue;
            var pg = new PathGeometry();
            pg.FillRule = FillRule.Nonzero;
            pg.AddGeometry(g);
            group.Children.Add(pg);
        }

        // ---------- 4) normalize into a SQUARE canvas with symmetric padding ----------
        // NINA renders dockable icons via a fixed-size Path with Stretch="Uniform", so the
        // geometry's aspect ratio decides how much of the square icon box it fills. A tall/narrow
        // geometry gets letterboxed horizontally and looks smaller than square neighbours. Output a
        // square (1:1) bounds with the content centred so it fills the icon box optimally.
        Rect b = group.Bounds;
        double side = Math.Max(b.Width, b.Height);
        double pad = side * 0.015;
        double total = side + pad * 2;

        double dx = pad - b.X + (total - b.Width) / 2.0;
        double dy = pad - b.Y + (total - b.Height) / 2.0;

        var final = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (Geometry child in group.Children)
        {
            var tr = new TranslateTransform(dx, dy);
            final.Children.Add(Geometry.Combine(child, Geometry.Empty, GeometryCombineMode.Union, tr));
        }

        Rect fb = final.Bounds;

        // ---------- 5) serialize XAML ----------
        var sb = new StringBuilder();
        sb.AppendLine("<ResourceDictionary");
        sb.AppendLine("    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
        sb.AppendLine();
        sb.AppendLine("    <!--");
        sb.AppendLine("      MLAstroRPA+TPPA dockable icon (single-colour GeometryGroup).");
        sb.AppendLine("      Generated by tools\\mlastro-iconbuild from three_stars.svg");
        sb.AppendLine("      (" + (pathFigures.Count == 0 ? "" : pathFigures.Count + " path + ") + texts.Count + " text glyphs).");
        sb.AppendLine("      Bounds: " + fb.X.ToString("0.##") + "," + fb.Y.ToString("0.##") + " -> " +
                      (fb.X + fb.Width).ToString("0.##") + "," + (fb.Y + fb.Height).ToString("0.##"));
        sb.AppendLine("    -->");
        sb.AppendLine("    <GeometryGroup x:Key=\"MLAstroTPPAIcon\" FillRule=\"Nonzero\">");
        foreach (Geometry child in final.Children)
        {
            sb.AppendLine("        " + ToPathGeometryElement(child));
        }
        sb.AppendLine("    </GeometryGroup>");
        sb.AppendLine("</ResourceDictionary>");

        string xaml = sb.ToString();
        File.WriteAllText(outXaml, xaml, new UTF8Encoding(false));
        Console.WriteLine("XAML written: " + outXaml);
        Console.WriteLine("bounds=" + fb);

        // ---------- 6) preview PNG ----------
        if (outPng != null)
            RenderPng(final, outPng);
        return 0;
    }

    static Geometry BuildGlyph(TextSpec t)
    {
        FontWeight fw = FontWeights.Normal;
        if (t.Weight >= 900) fw = FontWeights.Black;
        else if (t.Weight >= 800) fw = FontWeights.ExtraBold;
        else if (t.Weight >= 700) fw = FontWeights.Bold;
        else if (t.Weight >= 600) fw = FontWeights.SemiBold;

        try
        {
            var ft = new FormattedText(t.Label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily(t.Family), FontStyles.Normal, fw, FontStretches.Normal),
                t.FontSize, Brushes.Black, 1.0);

            // text-anchor:middle + dominant-baseline:central -> centre glyph box on (x,y)
            double ox = t.X - ft.Width / 2.0;
            double oy = t.Y - ft.Height / 2.0;
            return ft.BuildGeometry(new Point(ox, oy));
        }
        catch
        {
            Console.Error.WriteLine("WARN: cannot build glyph '" + t.Label + "' (font '" + t.Family + "') - skipped");
            return null;
        }
    }

    static PathGeometry ParsePathFigures(string d)
    {
        // d is pure SVG path data -> parse with WPF's mini-language parser.
        var geo = new PathGeometry();
        geo.FillRule = FillRule.Nonzero;
        var stream = StreamGeometry.Parse(d);
        geo.AddGeometry(stream);
        return geo;
    }

    static string ToPathGeometryElement(Geometry g)
    {
        var pg = new PathGeometry();
        pg.FillRule = FillRule.Nonzero;
        pg.AddGeometry(g);
        string figures = pg.ToString();
        // PathGeometry.ToString() prepends a fill-rule token "F0"/"F1" (e.g. "F1M..."). That token is
        // only valid when parsing a whole Path, NOT in the PathGeometry.Figures attribute - WPF throws
        // "Unexpected token 'F1...'" at XAML load. Strip it (the FillRule is set on the element itself).
        figures = Regex.Replace(figures, "^F[01]", "");
        return "<PathGeometry FillRule=\"Nonzero\" Figures=\"" + Escape(figures) + "\" />";
    }

    static void RenderPng(Geometry g, string path)
    {
        Rect b = g.Bounds;
        double pad = b.Width * 0.02;
        double w = b.Width + pad * 2, h = b.Height + pad * 2;
        int px = 1200;
        double scale = px / Math.Max(w, h);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w * scale, h * scale));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-b.X + pad, -b.Y + pad));
            dc.DrawGeometry(Brushes.Black, null, g);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
    }

    // ---------- helpers ----------
    static string Attr(string xml, string name, string def)
    {
        var m = Regex.Match(xml, "\\b" + name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : def;
    }
    static double AttrDouble(string xml, string name, double def)
    {
        string s = Attr(xml, name, null);
        double v;
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
    }
    static string Style(string style, string name, string def)
    {
        var m = Regex.Match(style ?? "", "\\b" + name + "\\s*:\\s*([^;]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : def;
    }
    static double StyleDouble(string style, string name, double def)
    {
        string s = Style(style, name, null);
        double v;
        if (s != null)
        {
            s = s.Trim();
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2).Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        }
        return def;
    }
    static string Escape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
