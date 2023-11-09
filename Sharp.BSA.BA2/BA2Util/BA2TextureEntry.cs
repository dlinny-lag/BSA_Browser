using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DirectXTex;
using SharpBSABA2.Extensions;
using SharpBSABA2.Utils;

namespace SharpBSABA2.BA2Util
{
    public class UnsupportedDDSException : Exception
    {
        public UnsupportedDDSException() : base() { }
        public UnsupportedDDSException(string message) : base(message) { }
        public UnsupportedDDSException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class BA2TextureEntry : ArchiveEntry
    {
        private const uint TILE_MODE_DEFAULT = 0x08;
        private const uint XBOX_BASE_ALIGNMENT = 256;
        private const uint XBOX_XDK_VERSION = 13202;

        /// <summary>
        /// Gets or sets whether to generate DDS header.
        /// </summary>
        public bool GenerateTextureHeader { get; set; } = true;
        public List<BA2TextureChunk> Chunks { get; private set; } = new List<BA2TextureChunk>();
        public long dataSizePosition = -1;

        public readonly byte unk1;
        public readonly byte numChunks;
        public readonly ushort chunkHdrLen;
        public readonly ushort height;
        public readonly ushort width;
        public readonly byte numMips;
        public readonly byte format;
        public readonly byte isCubemap;
        public readonly byte tileMode;

        // After testing it seems like ALL textures are compressed.
        public override bool Compressed => this.Chunks[0].packSz != 0;
        public override uint Size
        {
            get
            {
                uint size = this.GetHeaderSize();
                bool compressed = Chunks[0].packSz != 0;

                foreach (var chunk in Chunks)
                    size += compressed ? chunk.packSz : chunk.fullSz;

                return size;
            }
        }
        
        public bool IsLz4 { get; private set; }
        
        // Start of with the size of the DDS Magic + DDS header + if applicable DDS DXT10 header
        public override uint RealSize => this.GetHeaderSize() + (uint)this.Chunks.Sum(x => Math.Max(x.fullSz, x.packSz));
        public override uint DisplaySize => this.GetHeaderSize() + (uint)this.Chunks.Sum(x => x.fullSz);
        public override ulong Offset => this.Chunks[0].offset;

        public override ulong GetSizeInArchive(SharedExtractParams extractParams) => (ulong)Chunks.Sum(x => Compressed ? x.packSz : x.fullSz);

        public BA2TextureEntry(Archive ba2) : base(ba2)
        {
            nameHash = ba2.BinaryReader.ReadUInt32();
            Extension = new string(ba2.BinaryReader.ReadChars(4));
            dirHash = ba2.BinaryReader.ReadUInt32();

            if (ba2 is BA2 b) 
                IsLz4 = b.Header.CompressionFlag == 3;

            FullPath = dirHash > 0 ? $"{dirHash:X}_" : string.Empty;
            FullPath += $"{nameHash:X}.{Extension.TrimEnd('\0')}";
            FullPathOriginal = FullPath;

            unk1 = ba2.BinaryReader.ReadByte();
            numChunks = ba2.BinaryReader.ReadByte();
            chunkHdrLen = ba2.BinaryReader.ReadUInt16();
            height = ba2.BinaryReader.ReadUInt16();
            width = ba2.BinaryReader.ReadUInt16();
            numMips = ba2.BinaryReader.ReadByte();
            format = ba2.BinaryReader.ReadByte();
            isCubemap = ba2.BinaryReader.ReadByte();
            tileMode = ba2.BinaryReader.ReadByte();

            for (int i = 0; i < numChunks; i++)
            {
                this.Chunks.Add(new BA2TextureChunk(ba2.BinaryReader));
            }
        }

        public override string GetToolTipText()
        {
            string dxgi = Enum.GetName(typeof(DirectXTexUtility.DXGIFormat), format);

            return $"Name hash:\t {nameHash:X}\n" +
                $"Directory hash:\t {dirHash:X}\n" +
                $"DXGI format:\t {dxgi} ({format})\n" +
                $"Resolution:\t {width}x{height}\n" +
                $"Chunks:\t\t {numChunks}\n" +
                $"Chunk header len:\t {chunkHdrLen}\n" +
                $"Mipmaps:\t {numMips}\n" +
                $"Cubemap:\t {Convert.ToBoolean(isCubemap)}\n" +
                $"Tile mode:\t {tileMode}\n\n" +
                $"{nameof(unk1)}:\t\t {unk1}";
        }

        public bool IsFormatSupported()
        {
            return Enum.IsDefined(typeof(DirectXTexUtility.DXGIFormat), (uint)format);
        }

        private uint _headerSize = 0;
        
        private uint GetHeaderSize()
        {
            if (_headerSize > 0)
                return _headerSize;
            
            uint size = 0;
            
            size += (uint) Marshal.SizeOf(DirectXTexUtility.DDSHeader.DDSMagic);
            size += (uint) Marshal.SizeOf<DirectXTexUtility.DDSHeader>();
            var metadata = DirectXTexUtility.GenerateMetadata(width, height, numMips, (DirectXTexUtility.DXGIFormat) format, isCubemap == 1);
            var pixelFormat = DirectXTexUtility.GetPixelFormat(metadata);
            var hasDx10Header = DirectXTexUtility.HasDx10Header(pixelFormat);
            if (hasDx10Header)
                size += (uint) Marshal.SizeOf<DirectXTexUtility.DX10Header>();
            
            return _headerSize = size;
        }

        private void WriteHeader(BinaryWriter bw)
        {
            var metadata = DirectXTexUtility.GenerateMetadata(width, height, numMips, (DirectXTexUtility.DXGIFormat) format, isCubemap == 1);
            DirectXTexUtility.GenerateDDSHeader(metadata, DirectXTexUtility.DDSFlags.FORCEDX10EXTMISC2, out var header, out var header10);
            var headerBytes = DirectXTexUtility.EncodeDDSHeader(header, header10);

            bw.Write(headerBytes);
        }

        protected override void WriteDataToStream(Stream stream, SharedExtractParams extractParams, bool decompress = true)
        {
            var bw = new BinaryWriter(stream);
            var reader = extractParams.Reader;

            // Reset at start since value might still be in used for a bit after
            this.BytesWritten = 0;

            if (decompress && GenerateTextureHeader)
            {
                this.WriteHeader(bw);
                bw.Flush();
            }

            for (int i = 0; i < numChunks; i++)
            {
                bool isCompressed = this.Chunks[i].packSz != 0;
                ulong prev = this.BytesWritten;

                reader.BaseStream.Seek((long)this.Chunks[i].offset, SeekOrigin.Begin);

                if (decompress && isCompressed)
                {
                    if (IsLz4)
                        CompressionUtils.DecompressLZ4(
                            reader.BaseStream,
                            this.Chunks[i].packSz,
                            stream,
                            Chunks[i].fullSz,
                            bytesWritten => this.BytesWritten = prev + bytesWritten
                        );
                    else
                        CompressionUtils.Decompress(
                            reader.BaseStream,
                            this.Chunks[i].packSz,
                            stream,
                            bytesWritten => this.BytesWritten = prev + bytesWritten,
                            extractParams
                        );
                }
                else
                {
                    StreamUtils.WriteSectionToStream(reader.BaseStream,
                        Chunks[i].fullSz,
                        stream,
                        bytesWritten => this.BytesWritten = prev + bytesWritten);
                }
            }

            if (dataSizePosition > -1)
            {
                bw.WriteAt(dataSizePosition, (uint)bw.BaseStream.Length - 164);
            }
        }
    }
}
