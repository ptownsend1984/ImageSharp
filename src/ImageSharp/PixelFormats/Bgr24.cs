﻿// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SixLabors.ImageSharp.PixelFormats
{
    /// <summary>
    /// Pixel type containing three 8-bit unsigned normalized values ranging from 0 to 255.
    /// The color components are stored in blue, green, red order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Bgr24 : IPixel<Bgr24>
    {
        /// <summary>
        /// The blue component.
        /// </summary>
        public byte B;

        /// <summary>
        /// The green component.
        /// </summary>
        public byte G;

        /// <summary>
        /// The red component.
        /// </summary>
        public byte R;

        /// <summary>
        /// Initializes a new instance of the <see cref="Bgr24"/> struct.
        /// </summary>
        /// <param name="r">The red component.</param>
        /// <param name="g">The green component.</param>
        /// <param name="b">The blue component.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Bgr24(byte r, byte g, byte b)
        {
            this.B = b;
            this.G = g;
            this.R = r;
        }

        /// <inheritdoc/>
        public PixelOperations<Bgr24> CreatePixelOperations() => new PixelOperations<Bgr24>();

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Bgr24 other)
        {
            return this.B == other.B && this.G == other.G && this.R == other.R;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj?.GetType() == typeof(Bgr24) && this.Equals((Bgr24)obj);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = this.B;
                hashCode = (hashCode * 397) ^ this.G;
                hashCode = (hashCode * 397) ^ this.R;
                return hashCode;
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PackFromRgba32(Rgba32 source)
        {
            this = source.Bgr;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PackFromVector4(Vector4 vector)
        {
            var rgba = default(Rgba32);
            rgba.PackFromVector4(vector);
            this.PackFromRgba32(rgba);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 ToVector4()
        {
            return new Rgba32(this.R, this.G, this.B, 255).ToVector4();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ToRgb24(ref Rgb24 dest)
        {
            dest.R = this.R;
            dest.G = this.G;
            dest.B = this.B;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ToRgba32(ref Rgba32 dest)
        {
            dest.R = this.R;
            dest.G = this.G;
            dest.B = this.B;
            dest.A = 255;
        }

        /// <inheritdoc/>
        public void ToBgr24(ref Bgr24 dest)
        {
            dest.B = this.B;
            dest.G = this.G;
            dest.R = this.R;
        }

        /// <inheritdoc/>
        public void ToBgra32(ref Bgra32 dest)
        {
            dest.B = this.B;
            dest.G = this.G;
            dest.R = this.R;
            dest.A = 255;
        }
    }
}