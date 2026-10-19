using System;
using System.Buffers;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;

namespace ZstdSharp
{
    public unsafe class Decompressor : IDisposable
    {
        private readonly SafeDctxHandle handle;

        private GCHandle prefixHandle;

        public Decompressor()
        {
            handle = SafeDctxHandle.Create();
        }

        public void SetParameter(ZSTD_dParameter parameter, int value)
        {
            using var dctx = handle.Acquire();
            Methods.ZSTD_DCtx_setParameter(dctx, parameter, value).EnsureZstdSuccess();
        }

        public int GetParameter(ZSTD_dParameter parameter)
        {
            using var dctx = handle.Acquire();
            int value;
            Methods.ZSTD_DCtx_getParameter(dctx, parameter, &value).EnsureZstdSuccess();
            return value;
        }

        public void LoadDictionary(byte[] dict)
        {
            var dictReadOnlySpan = new ReadOnlySpan<byte>(dict);
            this.LoadDictionary(dictReadOnlySpan);
        }

        public void LoadDictionary(ReadOnlySpan<byte> dict)
        {
            using var dctx = handle.Acquire();
            fixed (byte* dictPtr = dict)
                Methods.ZSTD_DCtx_loadDictionary(dctx, dictPtr, (nuint)dict.Length).EnsureZstdSuccess();
        }

        /// <summary>
        /// References a prefix for the next decompressed frame (ZSTD_DCtx_refPrefix). Must be
        /// the same prefix referenced by <see cref="Compressor.RefPrefix(byte[])"/> during
        /// compression. Native semantics: the prefix applies to the next frame only and is
        /// referenced, not copied — re-reference before each frame. The supplied array is
        /// pinned and retained by the decompressor until replaced, cleared, or disposal, and
        /// must not be modified while in use. Pass null to clear. Remember to raise
        /// <see cref="ZSTD_dParameter.ZSTD_d_windowLogMax"/> to the windowLog used during
        /// compression when large windows are involved.
        /// </summary>
        /// <param name="prefix">Reference content used during compression, or null to clear.</param>
#nullable enable
        public void RefPrefix(byte[]? prefix)
        {
            using var dctx = handle.Acquire();
            FreePinnedPrefix();
            if (prefix == null || prefix.Length == 0)
            {
                Methods.ZSTD_DCtx_refPrefix(dctx, null, 0).EnsureZstdSuccess();
                return;
            }

            prefixHandle = GCHandle.Alloc(prefix, GCHandleType.Pinned);
            try
            {
                Methods.ZSTD_DCtx_refPrefix(dctx, (byte*)prefixHandle.AddrOfPinnedObject(), (nuint)prefix.Length)
                    .EnsureZstdSuccess();
            }
            catch
            {
                FreePinnedPrefix();
                throw;
            }
        }
#nullable restore

        private void FreePinnedPrefix()
        {
            if (prefixHandle.IsAllocated)
            {
                prefixHandle.Free();
            }
        }

        public static ulong GetDecompressedSize(ReadOnlySpan<byte> src)
        {
            fixed (byte* srcPtr = src)
                return Methods.ZSTD_decompressBound(srcPtr, (nuint)src.Length).EnsureContentSizeOk();
        }

        public static ulong GetDecompressedSize(ArraySegment<byte> src)
            => GetDecompressedSize((ReadOnlySpan<byte>)src);

        public static ulong GetDecompressedSize(byte[] src, int srcOffset, int srcLength)
            => GetDecompressedSize(new ReadOnlySpan<byte>(src, srcOffset, srcLength));

        public Span<byte> Unwrap(ReadOnlySpan<byte> src, int maxDecompressedSize = int.MaxValue)
        {
            var expectedDstSize = GetDecompressedSize(src);
            if (expectedDstSize > (ulong)maxDecompressedSize)
                throw new ZstdException(ZSTD_ErrorCode.ZSTD_error_dstSize_tooSmall,
                    $"Decompressed content size {expectedDstSize} is greater than {nameof(maxDecompressedSize)} {maxDecompressedSize}");
            if (expectedDstSize > Constants.MaxByteArrayLength)
                throw new ZstdException(ZSTD_ErrorCode.ZSTD_error_dstSize_tooSmall,
                    $"Decompressed content size {expectedDstSize} is greater than max possible byte array size {Constants.MaxByteArrayLength}");

            var dest = new byte[expectedDstSize];
            var length = Unwrap(src, dest);
            return new Span<byte>(dest, 0, length);
        }

        public int Unwrap(byte[] src, byte[] dest, int offset)
            => Unwrap(src, new Span<byte>(dest, offset, dest.Length - offset));

        public int Unwrap(ReadOnlySpan<byte> src, Span<byte> dest)
        {
            fixed (byte* srcPtr = src)
            fixed (byte* destPtr = dest)
            {
                using var dctx = handle.Acquire();
                return (int)Methods
                    .ZSTD_decompressDCtx(dctx, destPtr, (nuint)dest.Length, srcPtr, (nuint)src.Length)
                    .EnsureZstdSuccess();
            }
        }

        public int Unwrap(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
            => Unwrap(new ReadOnlySpan<byte>(src, srcOffset, srcLength), new Span<byte>(dst, dstOffset, dstLength));

        public bool TryUnwrap(byte[] src, byte[] dest, int offset, out int written)
            => TryUnwrap(src, new Span<byte>(dest, offset, dest.Length - offset), out written);

        public bool TryUnwrap(ReadOnlySpan<byte> src, Span<byte> dest, out int written)
        {
            fixed (byte* srcPtr = src)
            fixed (byte* destPtr = dest)
            {
                nuint returnValue;
                using (var dctx = handle.Acquire())
                {
                    returnValue =
                        Methods.ZSTD_decompressDCtx(dctx, destPtr, (nuint)dest.Length, srcPtr, (nuint)src.Length);
                }

                if (returnValue == unchecked(0 - (nuint)ZSTD_ErrorCode.ZSTD_error_dstSize_tooSmall))
                {
                    written = default;
                    return false;
                }

                returnValue.EnsureZstdSuccess();
                written = (int)returnValue;
                return true;
            }
        }

        public bool TryUnwrap(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength, out int written)
            => TryUnwrap(new ReadOnlySpan<byte>(src, srcOffset, srcLength), new Span<byte>(dst, dstOffset, dstLength), out written);

        public void Dispose()
        {
            handle.Dispose();
            FreePinnedPrefix();
            GC.SuppressFinalize(this);
        }

        internal nuint DecompressStream(ref ZSTD_inBuffer_s input, ref ZSTD_outBuffer_s output)
        {
            fixed (ZSTD_inBuffer_s* inputPtr = &input)
            fixed (ZSTD_outBuffer_s* outputPtr = &output)
            {
                using var dctx = handle.Acquire();
                return Methods.ZSTD_decompressStream(dctx, outputPtr, inputPtr).EnsureZstdSuccess();
            }
        }

        public void ResetStream()
        {
            using var dctx = handle.Acquire();
            Methods.ZSTD_DCtx_reset(dctx, ZSTD_ResetDirective.ZSTD_reset_session_only).EnsureZstdSuccess();
        }

        public OperationStatus UnwrapStream(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
        {
            using var dctx = handle.Acquire();
            bytesConsumed = bytesWritten = 0;

            fixed (byte* srcPtr = source)
            fixed (byte* dstPtr = destination)
            {
                var input = new ZSTD_inBuffer_s { src = srcPtr, size = (nuint)source.Length, pos = 0 };
                var output = new ZSTD_outBuffer_s { dst = dstPtr, size = (nuint)destination.Length, pos = 0 };

                while (output.pos != output.size)
                {
                    var remaining = Methods.ZSTD_decompressStream(dctx, &output, &input);
                    bytesConsumed = (int)input.pos;
                    bytesWritten = (int)output.pos;

                    if (Methods.ZSTD_isError(remaining))
                        return OperationStatus.InvalidData;

                    // input is finished
                    if (input.pos == input.size)
                    {
                        // end of frame
                        if (remaining == 0)
                            return OperationStatus.Done;

                        return OperationStatus.NeedMoreData;
                    }
                }

                return OperationStatus.DestinationTooSmall;
            }
        }
    }
}
