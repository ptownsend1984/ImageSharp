// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.Formats.Jpeg.Common;
using SixLabors.ImageSharp.Formats.Jpeg.Common.Decoder;
using SixLabors.ImageSharp.Formats.Jpeg.GolangPort.Components.Decoder;
using SixLabors.ImageSharp.MetaData;
using SixLabors.ImageSharp.MetaData.Profiles.Exif;
using SixLabors.ImageSharp.MetaData.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Primitives;

namespace SixLabors.ImageSharp.Formats.Jpeg.GolangPort
{
    /// <inheritdoc />
    /// <summary>
    /// Performs the jpeg decoding operation.
    /// </summary>
    internal sealed unsafe class OrigJpegDecoderCore : IRawJpegData
    {
        /// <summary>
        /// The maximum number of color components
        /// </summary>
        public const int MaxComponents = 4;

        /// <summary>
        /// The maximum number of quantization tables
        /// </summary>
        public const int MaxTq = 3;

        // Complex value type field + mutable + available to other classes = the field MUST NOT be private :P
#pragma warning disable SA1401 // FieldsMustBePrivate

        /// <summary>
        /// Encapsulates stream reading and processing data and operations for <see cref="OrigJpegDecoderCore"/>.
        /// It's a value type for imporved data locality, and reduced number of CALLVIRT-s
        /// </summary>
        public InputProcessor InputProcessor;
#pragma warning restore SA401

        /// <summary>
        /// The global configuration
        /// </summary>
        private readonly Configuration configuration;

        /// <summary>
        /// Holds marker buffers on read
        /// </summary>
        private readonly byte[] markerBuffer = new byte[2];

        /// <summary>
        /// Whether the image has a JFIF header
        /// </summary>
        private bool isJfif;

        /// <summary>
        /// Contains information about the JFIF marker
        /// </summary>
        private JFifMarker jFif;

        /// <summary>
        /// Whether the image has an Adobe marker
        /// </summary>
        private bool isAdobe;

        /// <summary>
        /// Contains information about the Adobe marker
        /// </summary>
        private AdobeMarker adobe;

        /// <summary>
        /// Whether the image has a EXIF header
        /// </summary>
        private bool isExif;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrigJpegDecoderCore" /> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="options">The options.</param>
        public OrigJpegDecoderCore(Configuration configuration, IJpegDecoderOptions options)
        {
            this.IgnoreMetadata = options.IgnoreMetadata;
            this.configuration = configuration ?? Configuration.Default;
            this.HuffmanTrees = OrigHuffmanTree.CreateHuffmanTrees();
            this.QuantizationTables = new Block8x8F[MaxTq + 1];
            this.Temp = new byte[2 * Block8x8F.Size];
        }

        /// <inheritdoc />
        public JpegColorSpace ColorSpace { get; private set; }

        /// <summary>
        /// Gets the component array
        /// </summary>
        public OrigComponent[] Components { get; private set; }

        /// <summary>
        /// Gets the huffman trees
        /// </summary>
        public OrigHuffmanTree[] HuffmanTrees { get; }

        /// <inheritdoc />
        public Block8x8F[] QuantizationTables { get; }

        /// <summary>
        /// Gets the temporary buffer used to store bytes read from the stream.
        /// TODO: Should be stack allocated, fixed sized buffer!
        /// </summary>
        public byte[] Temp { get; }

        /// <inheritdoc />
        public Size ImageSizeInPixels { get; private set; }

        /// <summary>
        /// Gets the number of MCU blocks in the image as <see cref="Size"/>.
        /// </summary>
        public Size ImageSizeInMCU { get; private set; }

        /// <inheritdoc />
        public int ComponentCount { get; private set; }

        IEnumerable<IJpegComponent> IRawJpegData.Components => this.Components;

        /// <summary>
        /// Gets the image height
        /// </summary>
        public int ImageHeight => this.ImageSizeInPixels.Height;

        /// <summary>
        /// Gets the image width
        /// </summary>
        public int ImageWidth => this.ImageSizeInPixels.Width;

        /// <summary>
        /// Gets the input stream.
        /// </summary>
        public Stream InputStream { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the image is interlaced (progressive)
        /// </summary>
        public bool IsProgressive { get; private set; }

        /// <summary>
        /// Gets the restart interval
        /// </summary>
        public int RestartInterval { get; private set; }

        /// <summary>
        /// Gets the number of MCU-s (Minimum Coded Units) in the image along the X axis
        /// </summary>
        public int MCUCountX => this.ImageSizeInMCU.Width;

        /// <summary>
        /// Gets the number of MCU-s (Minimum Coded Units) in the image along the Y axis
        /// </summary>
        public int MCUCountY => this.ImageSizeInMCU.Height;

        /// <summary>
        /// Gets the the total number of MCU-s (Minimum Coded Units) in the image.
        /// </summary>
        public int TotalMCUCount => this.MCUCountX * this.MCUCountY;

        /// <summary>
        /// Gets a value indicating whether the metadata should be ignored when the image is being decoded.
        /// </summary>
        public bool IgnoreMetadata { get; }

        /// <summary>
        /// Gets the <see cref="ImageMetaData"/> decoded by this decoder instance.
        /// </summary>
        public ImageMetaData MetaData { get; private set; }

        /// <summary>
        /// Decodes the image from the specified <see cref="Stream"/>  and sets
        /// the data to image.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="stream">The stream, where the image should be.</param>
        /// <returns>The decoded image.</returns>
        public Image<TPixel> Decode<TPixel>(Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            this.ParseStream(stream);

            return this.PostProcessIntoImage<TPixel>();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            for (int i = 0; i < this.HuffmanTrees.Length; i++)
            {
                this.HuffmanTrees[i].Dispose();
            }

            if (this.Components != null)
            {
                foreach (OrigComponent component in this.Components)
                {
                    component.Dispose();
                }
            }

            this.InputProcessor.Dispose();
        }

        /// <summary>
        /// Read metadata from stream and read the blocks in the scans into <see cref="OrigComponent.SpectralBlocks"/>.
        /// </summary>
        /// <param name="stream">The stream</param>
        /// <param name="metadataOnly">Whether to decode metadata only.</param>
        public void ParseStream(Stream stream, bool metadataOnly = false)
        {
            this.MetaData = new ImageMetaData();
            this.InputStream = stream;
            this.InputProcessor = new InputProcessor(stream, this.Temp);

            ushort fileMarker = this.ReadUint16();
            if (fileMarker != JpegConstants.Markers.SOI)
            {
                throw new ImageFormatException("Missing SOI marker.");
            }

            fileMarker = this.ReadUint16();
            while (fileMarker != JpegConstants.Markers.EOI)
            {
                // Get the marker length
                int remaining = this.ReadUint16() - 2;

                switch (fileMarker)
                {
                    case JpegConstants.Markers.APP0:
                        this.ProcessApplicationHeaderMarker(remaining);
                        break;

                    case JpegConstants.Markers.APP1:
                        this.ProcessApp1Marker(remaining);
                        break;

                    case JpegConstants.Markers.APP2:
                        this.ProcessApp2Marker(remaining);
                        break;
                    case JpegConstants.Markers.APP3:
                    case JpegConstants.Markers.APP4:
                    case JpegConstants.Markers.APP5:
                    case JpegConstants.Markers.APP6:
                    case JpegConstants.Markers.APP7:
                    case JpegConstants.Markers.APP8:
                    case JpegConstants.Markers.APP9:
                    case JpegConstants.Markers.APP10:
                    case JpegConstants.Markers.APP11:
                    case JpegConstants.Markers.APP12:
                    case JpegConstants.Markers.APP13:
                        this.InputProcessor.Skip(remaining);
                        break;

                    case JpegConstants.Markers.APP14:
                        this.ProcessApp14Marker(remaining);
                        break;

                    case JpegConstants.Markers.APP15:
                    case JpegConstants.Markers.COM:
                        this.InputProcessor.Skip(remaining);
                        break;

                    case JpegConstants.Markers.DQT:
                        if (metadataOnly)
                        {
                            this.InputProcessor.Skip(remaining);
                        }
                        else
                        {
                            this.ProcessDefineQuantizationTablesMarker(remaining);
                        }

                        break;

                    case JpegConstants.Markers.SOF0:
                    case JpegConstants.Markers.SOF1:
                    case JpegConstants.Markers.SOF2:
                        this.IsProgressive = fileMarker == OrigJpegConstants.Markers.SOF2;
                        this.ProcessStartOfFrameMarker(remaining);
                        if (metadataOnly && this.isJfif)
                        {
                            return;
                        }

                        break;

                    case JpegConstants.Markers.DHT:
                        if (metadataOnly)
                        {
                            this.InputProcessor.Skip(remaining);
                        }
                        else
                        {
                            this.ProcessDefineHuffmanTablesMarker(remaining);
                        }

                        break;

                    case JpegConstants.Markers.DRI:
                        if (metadataOnly)
                        {
                            this.InputProcessor.Skip(remaining);
                        }
                        else
                        {
                            this.ProcessDefineRestartIntervalMarker(remaining);
                        }

                        break;

                    case JpegConstants.Markers.SOS:
                        this.ProcessStartOfScanMarker(remaining);
                        break;
                }

                // Read on.
                fileMarker = this.FindNextFileMarker();
            }

            this.AssignResolution();
        }

        /// <summary>
        /// Finds the next file marker within the byte stream.
        /// </summary>
        /// <returns>The <see cref="ushort"/></returns>
        public ushort FindNextFileMarker()
        {
            this.InputProcessor.ReadFull(this.markerBuffer, 0, 2);

            if (this.InputProcessor.ReachedEOF)
            {
                return JpegConstants.Markers.EOI;
            }

            byte value = this.markerBuffer[1];
            if (this.markerBuffer[0] == JpegConstants.Markers.Prefix)
            {
                // According to Section B.1.1.2:
                // "Any marker may optionally be preceded by any number of fill bytes, which are bytes assigned code 0xFF."
                while (this.markerBuffer[1] == JpegConstants.Markers.Prefix)
                {
                    this.InputProcessor.ReadByte();
                    if (this.InputProcessor.ReachedEOF)
                    {
                        return JpegConstants.Markers.EOI;
                    }

                    this.markerBuffer[1] = value;
                }

                return (ushort)((this.markerBuffer[0] << 8) | this.markerBuffer[1]);
            }

            return (ushort)((this.markerBuffer[0] << 8) | this.markerBuffer[1]);
        }

        /// <summary>
        /// Processes the application header containing the JFIF identifier plus extra data.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApplicationHeaderMarker(int remaining)
        {
            if (remaining < 5)
            {
                // Skip the application header length
                this.InputProcessor.Skip(remaining);
                return;
            }

            this.InputProcessor.ReadFull(this.Temp, 0, 13);
            remaining -= 13;

            this.isJfif = this.Temp[0] == JpegConstants.Markers.JFif.J &&
                          this.Temp[1] == JpegConstants.Markers.JFif.F &&
                          this.Temp[2] == JpegConstants.Markers.JFif.I &&
                          this.Temp[3] == JpegConstants.Markers.JFif.F &&
                          this.Temp[4] == JpegConstants.Markers.JFif.Null;

            if (this.isJfif)
            {
                this.jFif = new JFifMarker
                {
                    MajorVersion = this.Temp[5],
                    MinorVersion = this.Temp[6],
                    DensityUnits = this.Temp[7],
                    XDensity = (short)((this.Temp[8] << 8) | this.Temp[9]),
                    YDensity = (short)((this.Temp[10] << 8) | this.Temp[11])
                };
            }

            // TODO: thumbnail
            if (remaining > 0)
            {
                this.InputProcessor.Skip(remaining);
            }
        }

        /// <summary>
        /// Processes the App1 marker retrieving any stored metadata
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApp1Marker(int remaining)
        {
            if (remaining < 6 || this.IgnoreMetadata)
            {
                // Skip the application header length
                this.InputProcessor.Skip(remaining);
                return;
            }

            byte[] profile = new byte[remaining];
            this.InputProcessor.ReadFull(profile, 0, remaining);

            if (profile[0] == JpegConstants.Markers.Exif.E &&
                profile[1] == JpegConstants.Markers.Exif.X &&
                profile[2] == JpegConstants.Markers.Exif.I &&
                profile[3] == JpegConstants.Markers.Exif.F &&
                profile[4] == JpegConstants.Markers.Exif.Null &&
                profile[5] == JpegConstants.Markers.Exif.Null)
            {
                this.isExif = true;
                this.MetaData.ExifProfile = new ExifProfile(profile);
            }
        }

        /// <summary>
        /// Processes the App2 marker retrieving any stored ICC profile information
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApp2Marker(int remaining)
        {
            // Length is 14 though we only need to check 12.
            const int Icclength = 14;
            if (remaining < Icclength || this.IgnoreMetadata)
            {
                this.InputProcessor.Skip(remaining);
                return;
            }

            byte[] identifier = new byte[Icclength];
            this.InputProcessor.ReadFull(identifier, 0, Icclength);
            remaining -= Icclength; // We have read it by this point

            if (identifier[0] == JpegConstants.Markers.ICC.I &&
                identifier[1] == JpegConstants.Markers.ICC.C &&
                identifier[2] == JpegConstants.Markers.ICC.C &&
                identifier[3] == JpegConstants.Markers.ICC.UnderScore &&
                identifier[4] == JpegConstants.Markers.ICC.P &&
                identifier[5] == JpegConstants.Markers.ICC.R &&
                identifier[6] == JpegConstants.Markers.ICC.O &&
                identifier[7] == JpegConstants.Markers.ICC.F &&
                identifier[8] == JpegConstants.Markers.ICC.I &&
                identifier[9] == JpegConstants.Markers.ICC.L &&
                identifier[10] == JpegConstants.Markers.ICC.E &&
                identifier[11] == JpegConstants.Markers.ICC.Null)
            {
                byte[] profile = new byte[remaining];
                this.InputProcessor.ReadFull(profile, 0, remaining);

                if (this.MetaData.IccProfile == null)
                {
                    this.MetaData.IccProfile = new IccProfile(profile);
                }
                else
                {
                    this.MetaData.IccProfile.Extend(profile);
                }
            }
            else
            {
                // Not an ICC profile we can handle. Skip the remaining bytes so we can carry on and ignore this.
                this.InputProcessor.Skip(remaining);
            }
        }

        /// <summary>
        /// Processes the application header containing the Adobe identifier
        /// which stores image encoding information for DCT filters.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApp14Marker(int remaining)
        {
            if (remaining < 12)
            {
                // Skip the application header length
                this.InputProcessor.Skip(remaining);
                return;
            }

            this.InputProcessor.ReadFull(this.Temp, 0, 12);
            remaining -= 12;

            this.isAdobe = this.Temp[0] == JpegConstants.Markers.Adobe.A &&
                           this.Temp[1] == JpegConstants.Markers.Adobe.D &&
                           this.Temp[2] == JpegConstants.Markers.Adobe.O &&
                           this.Temp[3] == JpegConstants.Markers.Adobe.B &&
                           this.Temp[4] == JpegConstants.Markers.Adobe.E;

            if (this.isAdobe)
            {
                this.adobe = new AdobeMarker
                {
                    DCTEncodeVersion = (short)((this.Temp[5] << 8) | this.Temp[6]),
                    APP14Flags0 = (short)((this.Temp[7] << 8) | this.Temp[8]),
                    APP14Flags1 = (short)((this.Temp[9] << 8) | this.Temp[10]),
                    ColorTransform = this.Temp[11]
                };
            }

            if (remaining > 0)
            {
                this.InputProcessor.Skip(remaining);
            }
        }

        /// <summary>
        /// Processes the Define Quantization Marker and tables. Specified in section B.2.4.1.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        /// <exception cref="ImageFormatException">
        /// Thrown if the tables do not match the header
        /// </exception>
        private void ProcessDefineQuantizationTablesMarker(int remaining)
        {
            while (remaining > 0)
            {
                bool done = false;

                remaining--;
                byte x = this.InputProcessor.ReadByte();
                int tq = x & 0x0F;
                if (tq > MaxTq)
                {
                    throw new ImageFormatException("Bad Tq value");
                }

                switch (x >> 4)
                {
                    case 0:
                        if (remaining < Block8x8F.Size)
                        {
                            done = true;
                            break;
                        }

                        remaining -= Block8x8F.Size;
                        this.InputProcessor.ReadFull(this.Temp, 0, Block8x8F.Size);

                        for (int i = 0; i < Block8x8F.Size; i++)
                        {
                            this.QuantizationTables[tq][i] = this.Temp[i];
                        }

                        break;
                    case 1:
                        if (remaining < 2 * Block8x8F.Size)
                        {
                            done = true;
                            break;
                        }

                        remaining -= 2 * Block8x8F.Size;
                        this.InputProcessor.ReadFull(this.Temp, 0, 2 * Block8x8F.Size);

                        for (int i = 0; i < Block8x8F.Size; i++)
                        {
                            this.QuantizationTables[tq][i] = (this.Temp[2 * i] << 8) | this.Temp[(2 * i) + 1];
                        }

                        break;
                    default:
                        throw new ImageFormatException("Bad Pq value");
                }

                if (done)
                {
                    break;
                }
            }

            if (remaining != 0)
            {
                throw new ImageFormatException("DQT has wrong length");
            }
        }

        /// <summary>
        /// Processes the Start of Frame marker.  Specified in section B.2.2.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessStartOfFrameMarker(int remaining)
        {
            if (this.ComponentCount != 0)
            {
                throw new ImageFormatException("Multiple SOF markers. Only single frame jpegs supported.");
            }

            switch (remaining)
            {
                case 6 + (3 * 1): // Grayscale image.
                    this.ComponentCount = 1;
                    break;
                case 6 + (3 * 3): // YCbCr or RGB image.
                    this.ComponentCount = 3;
                    break;
                case 6 + (3 * 4): // YCbCrK or CMYK image.
                    this.ComponentCount = 4;
                    break;
                default:
                    throw new ImageFormatException("Incorrect number of components");
            }

            this.InputProcessor.ReadFull(this.Temp, 0, remaining);

            // We only support 8-bit precision.
            if (this.Temp[0] != 8)
            {
                throw new ImageFormatException("Only 8-Bit precision supported.");
            }

            int height = (this.Temp[1] << 8) + this.Temp[2];
            int width = (this.Temp[3] << 8) + this.Temp[4];

            this.ImageSizeInPixels = new Size(width, height);

            if (this.Temp[5] != this.ComponentCount)
            {
                throw new ImageFormatException("SOF has wrong length");
            }

            this.Components = new OrigComponent[this.ComponentCount];

            for (int i = 0; i < this.ComponentCount; i++)
            {
                byte componentIdentifier = this.Temp[6 + (3 * i)];
                var component = new OrigComponent(componentIdentifier, i);
                component.InitializeCoreData(this);
                this.Components[i] = component;
            }

            int h0 = this.Components[0].HorizontalSamplingFactor;
            int v0 = this.Components[0].VerticalSamplingFactor;

            this.ImageSizeInMCU = this.ImageSizeInPixels.DivideRoundUp(8 * h0, 8 * v0);

            foreach (OrigComponent component in this.Components)
            {
                component.InitializeDerivedData(this);
            }

            this.ColorSpace = this.DeduceJpegColorSpace();
        }

        /// <summary>
        /// Processes a Define Huffman Table marker, and initializes a Huffman
        /// struct from its contents. Specified in section B.2.4.2.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessDefineHuffmanTablesMarker(int remaining)
        {
            while (remaining > 0)
            {
                if (remaining < 17)
                {
                    throw new ImageFormatException("DHT has wrong length");
                }

                this.InputProcessor.ReadFull(this.Temp, 0, 17);

                int tc = this.Temp[0] >> 4;
                if (tc > OrigHuffmanTree.MaxTc)
                {
                    throw new ImageFormatException("Bad Tc value");
                }

                int th = this.Temp[0] & 0x0f;
                if (th > OrigHuffmanTree.MaxTh || (!this.IsProgressive && (th > 1)))
                {
                    throw new ImageFormatException("Bad Th value");
                }

                int huffTreeIndex = (tc * OrigHuffmanTree.ThRowSize) + th;
                this.HuffmanTrees[huffTreeIndex].ProcessDefineHuffmanTablesMarkerLoop(
                    ref this.InputProcessor,
                    this.Temp,
                    ref remaining);
            }
        }

        /// <summary>
        /// Processes the SOS (Start of scan marker).
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        /// <exception cref="ImageFormatException">
        /// Missing SOF Marker
        /// SOS has wrong length
        /// </exception>
        private void ProcessStartOfScanMarker(int remaining)
        {
            var scan = default(OrigJpegScanDecoder);
            OrigJpegScanDecoder.InitStreamReading(&scan, this, remaining);

            // TODO: InputProcessor can probably live within this struct only
            this.InputProcessor.Bits = default(Bits);
            scan.DecodeBlocks(this);
        }

        /// <summary>
        /// Assigns the horizontal and vertical resolution to the image if it has a JFIF header or EXIF metadata.
        /// </summary>
        private void AssignResolution()
        {
            if (this.isExif)
            {
                ExifValue horizontal = this.MetaData.ExifProfile.GetValue(ExifTag.XResolution);
                ExifValue vertical = this.MetaData.ExifProfile.GetValue(ExifTag.YResolution);
                double horizontalValue = horizontal != null ? ((Rational)horizontal.Value).ToDouble() : 0;
                double verticalValue = vertical != null ? ((Rational)vertical.Value).ToDouble() : 0;

                if (horizontalValue > 0 && verticalValue > 0)
                {
                    this.MetaData.HorizontalResolution = horizontalValue;
                    this.MetaData.VerticalResolution = verticalValue;
                }
            }
            else if (this.jFif.XDensity > 0 && this.jFif.YDensity > 0)
            {
                this.MetaData.HorizontalResolution = this.jFif.XDensity;
                this.MetaData.VerticalResolution = this.jFif.YDensity;
            }
        }

        /// <summary>
        /// Processes the DRI (Define Restart Interval Marker) Which specifies the interval between RSTn markers, in
        /// macroblocks
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessDefineRestartIntervalMarker(int remaining)
        {
            if (remaining != 2)
            {
                throw new ImageFormatException($"DRI has wrong length: {remaining}");
            }

            this.RestartInterval = this.ReadUint16();
        }

        private JpegColorSpace DeduceJpegColorSpace()
        {
            switch (this.ComponentCount)
            {
                case 1:
                    return JpegColorSpace.GrayScale;
                case 3:
                    if (!this.isAdobe || this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformYCbCr)
                    {
                        return JpegColorSpace.YCbCr;
                    }

                    if (this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformUnknown)
                    {
                        return JpegColorSpace.RGB;
                    }

                    break;
                case 4:
                    if (this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformYcck)
                    {
                        return JpegColorSpace.Ycck;
                    }

                    return JpegColorSpace.Cmyk;
            }

            throw new ImageFormatException($"Unsupported color mode. Max components 4; found {this.ComponentCount}."
                                           + "JpegDecoder only supports YCbCr, RGB, YccK, CMYK and Grayscale color spaces.");
        }

        private Image<TPixel> PostProcessIntoImage<TPixel>()
            where TPixel : struct, IPixel<TPixel>
        {
            using (var postProcessor = new JpegImagePostProcessor(this))
            {
                var image = new Image<TPixel>(this.configuration, this.ImageWidth, this.ImageHeight, this.MetaData);
                postProcessor.PostProcess(image.Frames.RootFrame);
                return image;
            }
        }

        /// <summary>
        /// Reads a <see cref="ushort"/> from the stream advancing it by two bytes
        /// </summary>
        /// <returns>The <see cref="ushort"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort ReadUint16()
        {
            this.InputProcessor.ReadFull(this.markerBuffer, 0, 2);
            return (ushort)((this.markerBuffer[0] << 8) | this.markerBuffer[1]);
        }
    }
}