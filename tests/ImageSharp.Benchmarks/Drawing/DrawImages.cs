// <copyright file="DrawBeziers.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace SixLabors.ImageSharp.Benchmarks
{
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Numerics;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;

    using SixLabors.ImageSharp.PixelFormats;

    public class DrawImagesBase : BenchmarkBase
    {
        protected byte[] pngBytes;
        protected Point[] drawPoints;
        protected Primitives.Point[] drawPointPrimitives;
        protected const int destWidth = 256;
        protected const int destHeight = 256;

        [GlobalSetup]
        public void ReadImages()
        {
            if (this.pngBytes == null)
            {
                this.pngBytes = File.ReadAllBytes("../../../../../../../../Images/Input/Png/red16x16.png");
            }
            if (this.drawPoints == null)
            {
                var allDrawPoints = new List<Point>();
                var allDrawPointPrimitives = new List<Primitives.Point>();

                using (FileStream fs = File.OpenRead("../../../../../../../../Images/Input/Txt/DrawImagePoints.txt"))
                using (var sr = new StreamReader(fs))
                {
                    string currentLine;
                    while ((currentLine = sr.ReadLine()) != null)
                    {
                        string[] split = currentLine.Split(',');
                        allDrawPoints.Add(new Point(int.Parse(split[0]), int.Parse(split[1])));
                        allDrawPointPrimitives.Add(new Primitives.Point(int.Parse(split[0]), int.Parse(split[1])));
                    };
                }

                this.drawPoints = allDrawPoints.ToArray();
                this.drawPointPrimitives = allDrawPointPrimitives.ToArray();
            }
        }

    }

    public class DrawImages : DrawImagesBase
    {
        [Benchmark(Baseline = true, Description = "System.Drawing Draw Images")]
        public void DrawImageSystemDrawing()
        {

            Bitmap icon;
            using (var ms = new MemoryStream())
            {
                ms.Write(this.pngBytes, 0, this.pngBytes.Length);
                icon = new Bitmap(ms);
            }

            using (icon)
            {
                using (var destination = new Bitmap(destWidth, destHeight))
                {
                    using (var graphics = Graphics.FromImage(destination))
                    {
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        graphics.CompositingQuality = CompositingQuality.HighSpeed;
                        graphics.SmoothingMode = SmoothingMode.None;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

                        foreach (Point p in this.drawPoints)
                        {
                            graphics.DrawImage(icon, p);
                        }
                    }

                    using (var ms = new MemoryStream())
                    {
                        destination.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                    }
                }
            }
        }

        [Benchmark(Description = "ImageSharp Draw Images")]
        public void DrawImageCore()
        {
            using (var icon = ImageSharp.Image.Load(this.pngBytes))
            {
                using (var image = new Image<Rgba32>(destWidth, destHeight))
                {
                    var size = new Primitives.Size(16, 16);
                    var graphicOptions = new GraphicsOptions
                    {
                        Antialias = false,
                        BlenderMode = PixelBlenderMode.Normal
                    };
                    foreach (Primitives.Point p in this.drawPointPrimitives)
                    {
                        image.Mutate(x => x.DrawImage(icon, size, p, graphicOptions));
                    }

                    using (var ms = new MemoryStream())
                    {
                        image.SaveAsBmp(ms);
                    }
                }
            }
        }
    }

    public class DrawImagesParallel : DrawImagesBase
    {

        private const int Concurrency = 2;

        [Benchmark(Baseline = true, Description = "System.Drawing Draw Images Parallel")]
        public void DrawImageSystemDrawing()
        {
            Parallel.For(0, Concurrency, new ParallelOptions(), (i, state) =>
            {
                Bitmap icon;
                using (var ms = new MemoryStream())
                {
                    ms.Write(this.pngBytes, 0, this.pngBytes.Length);
                    icon = new Bitmap(ms);
                }

                using (icon)
                {
                    using (var destination = new Bitmap(destWidth, destHeight))
                    {
                        using (var graphics = Graphics.FromImage(destination))
                        {
                            graphics.CompositingMode = CompositingMode.SourceOver;
                            graphics.CompositingQuality = CompositingQuality.HighSpeed;
                            graphics.SmoothingMode = SmoothingMode.None;
                            graphics.PixelOffsetMode = PixelOffsetMode.Half;
                            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

                            foreach (Point p in this.drawPoints)
                            {
                                graphics.DrawImage(icon, p);
                            }
                        }

                        using (var ms = new MemoryStream())
                        {
                            destination.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        }
                    }
                }
            });
        }

        [Benchmark(Description = "ImageSharp Draw Images Parallel")]
        public void DrawImageCore()
        {
            Parallel.For(0, Concurrency, new ParallelOptions(), (i, state) =>
            {
                using (var icon = ImageSharp.Image.Load(this.pngBytes))
                {
                    using (var image = new Image<Rgba32>(destWidth, destHeight))
                    {
                        var size = new Primitives.Size(16, 16);
                        var graphicOptions = new GraphicsOptions
                        {
                            Antialias = false,
                            BlenderMode = PixelBlenderMode.Normal
                        };
                        foreach (Primitives.Point p in this.drawPointPrimitives)
                        {
                            image.Mutate(x => x.DrawImage(icon, size, p, graphicOptions));
                        }

                        using (var ms = new MemoryStream())
                        {
                            image.SaveAsBmp(ms);
                        }
                    }
                }
            });
        }
    }
}